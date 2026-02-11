using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpExtension.IO;
using SharpExtension;
using SharpExtension.Collections;

namespace QQS_UI.Core
{
    /// <summary>
    /// Compact note reference - 12 bytes on disk
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NoteRef
    {
        public uint Start;
        public uint End;
        public ushort Track;
        public byte Key;
        public byte _padding;
    }

    /// <summary>
    /// File header for disk cache format
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct CacheHeader
    {
        public long Magic;      // 'QSR0' 
        public int Version;     // 1
        public ushort TrackCount;
        public ushort Division;
        public uint MidiTime;
        public long NoteCount;
        public long TemposCount;
        public long Offsets128; // Offset to key offset table
        public long Counts128;  // Offset to key count table
        public long TemposOffset; // Offset to tempos
        public long DataStart;  // Start of note data
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TempoEntry
    {
        public uint Tick;
        public uint Value;
    }

    /// <summary>
    /// Disk-cached MIDI loader with streaming I/O.
    /// - Original MIDI read via buffered FileStream (no memory map)
    /// - Parsed data written to disk cache in temp folder
    /// - Rendering uses buffered FileStream from cache (no memory map)
    /// - Cache auto-deleted on dispose
    /// </summary>
    public unsafe class RenderFile : IDisposable
    {
        public ushort TrackCount;
        public ushort Division;
        public uint MidiTime = 0;
        public long NoteCount = 0;
        
        // Cache file
        private string cacheFilePath = null!;
        
        // Memory-mapped cache for rendering (zero-copy)
        private MemoryMappedFile? cacheMmf;
        private MemoryMappedViewAccessor? cacheAccessor;
        private byte* cacheBasePtr = null;
        
        // Note data info (read from cache header) - 256 keys support
        public long[] NoteCounts = new long[256];
        public long[] KeyDataOffsets = new long[256];
        public long TemposOffset;
        public long TemposCount;
        
        // Direct pointers into memory-mapped cache - 256 keys support
        public NoteRef*[] NotePointers = new NoteRef*[256];
        public TempoEntry* TemposPtr;
        
        // UnmanagedList wrapper for tempos (for compatibility)
        public UnmanagedList<Tempo> Tempos = new UnmanagedList<Tempo>();

        private void Parse()
        {
            ParallelOptions opt = new()
            {
                MaxDegreeOfParallelism = Global.MaxMIDILoaderConcurrency
            };
            long maxMidiTime = 0;
            long totalNoteCount = 0;

            // Create temp directory
            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            Directory.CreateDirectory(tempDir);
            cacheFilePath = Path.Combine(tempDir, $"midi_cache_{Guid.NewGuid():N}.tmp");
            
            Console.WriteLine($"使用磁盘缓存: {cacheFilePath}");

            // === First Pass: Count notes per key using buffered FileStream ===
            Console.WriteLine("第一遍扫描: 统计音符...");
            long[] noteCountsPerKey = new long[256];
            
            using (var fs = new FileStream(MidiPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(fs, Encoding.UTF8, true))
            {
                // Parse header
                if (reader.ReadByte() != 'M' || reader.ReadByte() != 'T' || 
                    reader.ReadByte() != 'h' || reader.ReadByte() != 'd')
                    throw new Exception("Invalid MIDI Header");
                
                uint hdrSize = ReadUInt32BE(reader);
                if (hdrSize != 6) throw new Exception("Invalid Header Size");
                
                ushort format = ReadUInt16BE(reader);
                if (format == 2) throw new Exception("Format 2 MIDI not supported");
                
                TrackCount = ReadUInt16BE(reader);
                Division = ReadUInt16BE(reader);
                
                if (Division > 32767) throw new Exception("SMPTE Division not supported");

                // Get track positions
                var trackOffsets = new long[TrackCount];
                var trackSizes = new long[TrackCount];
                
                for (int i = 0; i < TrackCount; i++)
                {
                    if (reader.ReadByte() != 'M' || reader.ReadByte() != 'T' ||
                        reader.ReadByte() != 'r' || reader.ReadByte() != 'k')
                        throw new Exception($"Invalid Track Header @ {i}");
                    
                    uint size = ReadUInt32BE(reader);
                    trackOffsets[i] = fs.Position;
                    trackSizes[i] = size;
                    fs.Seek(size, SeekOrigin.Current);
                }

                // Count notes per track in parallel
                _ = Parallel.For(0, TrackCount, opt, (i) =>
                {
                    using var trackFs = new FileStream(MidiPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                    trackFs.Seek(trackOffsets[i], SeekOrigin.Begin);
                    
                    var trackReader = new BinaryReader(trackFs, Encoding.UTF8, true);
                    long endPos = trackOffsets[i] + trackSizes[i];
                    
                    uint trkTime = 0;
                    byte prev = 0;
                    Span<long> localCounts = stackalloc long[256];
                    
                    while (trackFs.Position < endPos)
                    {
                        trkTime += ReadVLQ(trackReader);
                        byte comm = trackReader.ReadByte();
                        if (comm < 0x80)
                        {
                            trackFs.Seek(-1, SeekOrigin.Current);
                            comm = prev;
                        }
                        else
                        {
                            prev = comm;
                        }
                        
                        switch (comm & 0xF0)
                        {
                            case 0x90: // Note On
                                byte k = trackReader.ReadByte();
                                byte v = trackReader.ReadByte();
                                if (v != 0) localCounts[k]++;
                                break;
                            case 0x80: // Note Off
                            case 0xA0:
                            case 0xB0:
                            case 0xE0:
                                trackFs.Seek(2, SeekOrigin.Current);
                                break;
                            case 0xC0:
                            case 0xD0:
                                trackFs.Seek(1, SeekOrigin.Current);
                                break;
                            default:
                                if (comm == 0xFF) // Meta
                                {
                                    byte type = trackReader.ReadByte();
                                    if (type == 0x2F) // End
                                    {
                                        trackFs.Seek(1, SeekOrigin.Current);
                                    }
                                    else if (type == 0x51) // Tempo
                                    {
                                        trackFs.Seek(4, SeekOrigin.Current);
                                    }
                                    else
                                    {
                                        uint len = ReadVLQ(trackReader);
                                        trackFs.Seek(len, SeekOrigin.Current);
                                    }
                                }
                                else if (comm == 0xF0 || comm == 0xF7) // SysEx
                                {
                                    while (trackReader.ReadByte() != 0xF7) { }
                                }
                                break;
                        }
                    }
                    
                    for (int k = 0; k < 256; k++)
                        if (localCounts[k] > 0)
                            Interlocked.Add(ref noteCountsPerKey[k], localCounts[k]);
                    
                    Interlocked.Add(ref totalNoteCount, localCounts.ToArray().Sum());
                    
                    long initMax, compMax;
                    do
                    {
                        initMax = Interlocked.Read(ref maxMidiTime);
                        compMax = Math.Max(initMax, trkTime);
                    }
                    while (initMax < compMax && Interlocked.CompareExchange(ref maxMidiTime, compMax, initMax) != initMax);
                });
            }

            // === Second Pass: Write notes to disk cache ===
            Console.WriteLine("第二遍扫描: 写入磁盘缓存...");
            
            // Calculate layout (256 keys)
            long dataStart = sizeof(CacheHeader) + 256 * sizeof(long) * 2;
            long currentOffset = dataStart;
            
            for (int k = 0; k < 256; k++)
            {
                KeyDataOffsets[k] = currentOffset;
                currentOffset += noteCountsPerKey[k] * sizeof(NoteRef);
            }
            
            long temposOffset = currentOffset;
            long totalFileSize = temposOffset + 1024 * sizeof(TempoEntry); // Pre-allocate for tempos
            
            // Create cache file and pre-allocate
            FileStream? cacheFs = null;
            BinaryWriter? writer = null;
            
            try
            {
                // 使用更大的缓冲区（1MB）进行流式写入，避免memory-mapped file导致的Standby List污染
                cacheFs = new FileStream(cacheFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                cacheFs.SetLength(totalFileSize);
                
                writer = new BinaryWriter(cacheFs, Encoding.UTF8, true);
                
                // Write placeholder header (will update later)
                long headerPos = cacheFs.Position;
                var header = new CacheHeader
                {
                    Magic = 0x30525351,
                    Version = 1,
                    TrackCount = TrackCount,
                    Division = Division,
                    MidiTime = (uint)maxMidiTime,
                    NoteCount = totalNoteCount,
                    TemposCount = 0,
                    Offsets128 = sizeof(CacheHeader),
                    Counts128 = sizeof(CacheHeader) + 256 * sizeof(long),
                    TemposOffset = temposOffset,
                    DataStart = dataStart
                };
                WriteHeader(writer, header);
                
                // Write key offsets (256 keys) - 必须写入全部256个
                for (int k = 0; k < 256; k++)
                    writer.Write(KeyDataOffsets[k]);
                
                // Write key counts (256 keys) - 必须写入全部256个
                for (int k = 0; k < 256; k++)
                    writer.Write(noteCountsPerKey[k]);
                
                // Parse and write notes - 按轨道流式写入，立即释放内存
                Console.WriteLine($"开始并行解析 {TrackCount} 个音轨...");
                var trackTempos = new List<TempoEntry>[TrackCount];
                for (int i = 0; i < TrackCount; i++) trackTempos[i] = new List<TempoEntry>();
                
                // 为文件写入创建全局锁，保证 FileStream 线程安全
                var cacheFileLock = new object();
                
                // 用于追踪每个键已写入的音符数量
                var keyWrittenCounts = new long[256];
                
                try
                {
                    // Per-key write position trackers (need atomic updates)
                    var keyWritePositions = new long[256];
                    
                    // Pre-compute track data positions
                    var trackDataOffsets = new long[TrackCount];
                    var trackDataSizes = new long[TrackCount];
                    using (var fs = new FileStream(MidiPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                    {
                        fs.Seek(8 + 6, SeekOrigin.Begin);
                        for (int i = 0; i < TrackCount; i++)
                        {
                            fs.Seek(4, SeekOrigin.Current);
                            uint size = ReadUInt32BE(new BinaryReader(fs, Encoding.UTF8, true));
                            trackDataOffsets[i] = fs.Position;
                            trackDataSizes[i] = size;
                            fs.Seek(size, SeekOrigin.Current);
                        }
                    }
                    
                    int completedTracks = 0;
                    object consoleLock = new object();
                    long totalNotesWritten = 0;
                    
                    _ = Parallel.For(0, TrackCount, opt, (trackIdx) =>
                    {
                        // 每个轨道独立的音符缓冲区（按键分组）
                        var trackNotes = new List<NoteRef>[256];
                        
                        using var fs = new FileStream(MidiPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                        fs.Seek(trackDataOffsets[trackIdx], SeekOrigin.Begin);
                        
                        var reader = new BinaryReader(fs, Encoding.UTF8, true);
                        long trackEnd = trackDataOffsets[trackIdx] + trackDataSizes[trackIdx];
                        
                        var pendingPos = new ForwardLinkedList<long>[256];
                        var pendingStart = new ForwardLinkedList<uint>[256];
                        for (int k = 0; k < 256; k++)
                        {
                            pendingPos[k] = new ForwardLinkedList<long>();
                            pendingStart[k] = new ForwardLinkedList<uint>();
                            trackNotes[k] = new List<NoteRef>(128); // 小初始容量
                        }
                        
                        var localTempos = new List<TempoEntry>();
                        long localNoteCount = 0;
                        
                        uint trkTime = 0;
                        byte prev = 0;
                        
                        while (fs.Position < trackEnd)
                        {
                            trkTime += ReadVLQ(reader);
                            byte comm = reader.ReadByte();
                            if (comm < 0x80)
                            {
                                fs.Seek(-1, SeekOrigin.Current);
                                comm = prev;
                            }
                            else
                            {
                                prev = comm;
                            }
                            
                            switch (comm & 0xF0)
                            {
                                case 0x80:
                                    {
                                        byte k = reader.ReadByte();
                                        reader.ReadByte();
                                        if (pendingPos[k].Any())
                                        {
                                            long pos = pendingPos[k].Pop();
                                            uint start = pendingStart[k].Pop();
                                            var note = new NoteRef 
                                            { 
                                                Start = start, 
                                                End = trkTime, 
                                                Track = (ushort)trackIdx, 
                                                Key = k 
                                            };
                                            trackNotes[k].Add(note);
                                            localNoteCount++;
                                        }
                                    }
                                    break;
                                    
                                case 0x90:
                                    {
                                        byte k = reader.ReadByte();
                                        byte v = reader.ReadByte();
                                        if (v != 0)
                                        {
                                            long pos = Interlocked.Increment(ref keyWritePositions[k]) - 1;
                                            pendingPos[k].Add(pos);
                                            pendingStart[k].Add(trkTime);
                                        }
                                        else
                                        {
                                            if (pendingPos[k].Any())
                                            {
                                                long pos = pendingPos[k].Pop();
                                                uint start = pendingStart[k].Pop();
                                                var note = new NoteRef 
                                                { 
                                                    Start = start, 
                                                    End = trkTime, 
                                                    Track = (ushort)trackIdx, 
                                                    Key = k 
                                                };
                                                trackNotes[k].Add(note);
                                                localNoteCount++;
                                            }
                                        }
                                    }
                                    break;
                                    
                                case 0xA0:
                                case 0xB0:
                                case 0xE0:
                                    fs.Seek(2, SeekOrigin.Current);
                                    break;
                                case 0xC0:
                                case 0xD0:
                                    fs.Seek(1, SeekOrigin.Current);
                                    break;
                                    
                                default:
                                    if (comm == 0xFF)
                                    {
                                        byte type = reader.ReadByte();
                                        if (type == 0x2F)
                                        {
                                            reader.ReadByte();
                                            for (int k = 0; k < 256; k++)
                                            {
                                                while (pendingPos[k].Any())
                                                {
                                                    long pos = pendingPos[k].Pop();
                                                    uint start = pendingStart[k].Pop();
                                                    var note = new NoteRef 
                                                    { 
                                                        Start = start, 
                                                        End = trkTime, 
                                                        Track = (ushort)trackIdx, 
                                                        Key = (byte)k 
                                                    };
                                                    trackNotes[k].Add(note);
                                                    localNoteCount++;
                                                }
                                            }
                                        }
                                        else if (type == 0x51)
                                        {
                                            uint len = ReadVLQ(reader);
                                            byte b1 = reader.ReadByte();
                                            byte b2 = reader.ReadByte();
                                            byte b3 = reader.ReadByte();
                                            uint tempo = (uint)((b1 << 16) | (b2 << 8) | b3);
                                            localTempos.Add(new TempoEntry { Tick = trkTime, Value = tempo });
                                        }
                                        else
                                        {
                                            uint len = ReadVLQ(reader);
                                            fs.Seek(len, SeekOrigin.Current);
                                        }
                                    }
                                    else if (comm == 0xF0 || comm == 0xF7)
                                    {
                                        while (reader.ReadByte() != 0xF7) { }
                                    }
                                    break;
                            }
                        }
                        
                        trackTempos[trackIdx] = localTempos;
                        Interlocked.Add(ref totalNotesWritten, localNoteCount);
                        
                        // 轨道解析完成后，立即排序并写入其音符数据，然后释放内存
                        for (int k = 0; k < 256; k++)
                        {
                            if (trackNotes[k].Count == 0) continue;
                            
                            // 排序该轨道在此键上的音符
                            trackNotes[k].Sort((a, b) => a.Start.CompareTo(b.Start));
                            
                            // 写入文件（需要全局锁保护 FileStream 并发访问）
                            lock (cacheFileLock)
                            {
                                long writeOffset = KeyDataOffsets[k] + keyWrittenCounts[k] * sizeof(NoteRef);
                                cacheFs.Position = writeOffset;
                                
                                foreach (var note in trackNotes[k])
                                {
                                    writer.Write(note.Start);
                                    writer.Write(note.End);
                                    writer.Write(note.Track);
                                    writer.Write(note.Key);
                                    writer.Write(note._padding);
                                }
                                
                                keyWrittenCounts[k] += trackNotes[k].Count;
                            }
                            
                            // 立即释放此键的内存
                            trackNotes[k].Clear();
                        }
                        
                        // 清空整个轨道数据
                        trackNotes = null;
                        
                        int completed = Interlocked.Increment(ref completedTracks);
                        lock (consoleLock)
                        {
                            Console.WriteLine($"音轨 #{trackIdx} 解析并写入完成 ({completed}/{TrackCount}). 写入 {localNoteCount} 个音符.");
                        }
                        
                        // 每10个轨道强制GC一次
                        if (completed % 10 == 0)
                        {
                            GC.Collect(2, GCCollectionMode.Forced, false);
                            GC.WaitForPendingFinalizers();
                        }
                    });
                    
                    // 二次重新排序（保证每个键内按 Start 升序），但只保留一个共享缓冲区，避免爆内存
                    Console.WriteLine("重新排序音符数据（单缓冲区，顺序处理每个键以限制内存峰值）...");
                    long maxNotesPerKey = noteCountsPerKey.Max();
                    if (maxNotesPerKey > 0)
                    {
                        const int NoteRefSize = 12;
                        var notesBuffer = new NoteRef[maxNotesPerKey];
                        var rawBuffer = new byte[maxNotesPerKey * NoteRefSize];

                        for (int k = 0; k < 256; k++)
                        {
                            long count = noteCountsPerKey[k];
                            if (count == 0) continue;

                            int bytes = checked((int)(count * NoteRefSize));

                            // 读取该键的所有音符到共享缓冲区
                            lock (cacheFileLock)
                            {
                                cacheFs.Position = KeyDataOffsets[k];
                                int bytesRead = cacheFs.Read(rawBuffer, 0, bytes);
                                if (bytesRead != bytes)
                                    throw new IOException($"键{k}读取数据不完整: 期望{bytes}字节，实际{bytesRead}字节");
                            }

                            // 解析到 NoteRef 数组
                            for (long i = 0; i < count; i++)
                            {
                                int offset = (int)(i * NoteRefSize);
                                notesBuffer[i] = new NoteRef
                                {
                                    Start = BitConverter.ToUInt32(rawBuffer, offset),
                                    End = BitConverter.ToUInt32(rawBuffer, offset + 4),
                                    Track = BitConverter.ToUInt16(rawBuffer, offset + 8),
                                    Key = rawBuffer[offset + 10],
                                    _padding = rawBuffer[offset + 11]
                                };
                            }

                            // 按 Start 升序排序
                            Array.Sort(notesBuffer, 0, (int)count, Comparer<NoteRef>.Create((a, b) => a.Start.CompareTo(b.Start)));

                            // 写回原始缓冲
                            for (long i = 0; i < count; i++)
                            {
                                int offset = (int)(i * NoteRefSize);
                                BitConverter.TryWriteBytes(new Span<byte>(rawBuffer, offset, 4), notesBuffer[i].Start);
                                BitConverter.TryWriteBytes(new Span<byte>(rawBuffer, offset + 4, 4), notesBuffer[i].End);
                                BitConverter.TryWriteBytes(new Span<byte>(rawBuffer, offset + 8, 2), notesBuffer[i].Track);
                                rawBuffer[offset + 10] = notesBuffer[i].Key;
                                rawBuffer[offset + 11] = notesBuffer[i]._padding;
                            }

                            // 写回文件
                            lock (cacheFileLock)
                            {
                                cacheFs.Position = KeyDataOffsets[k];
                                cacheFs.Write(rawBuffer, 0, bytes);
                            }
                        }
                    }

                    Console.WriteLine("音符数据写入与排序完成.");
                    cacheFs.Flush();
                    
                    // 强制GC回收
                    GC.Collect(2, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, true);
                }
                finally
                {
                    // 确保所有资源都释放
                }
                
                // Force flush to disk
                cacheFs.Flush(true);
                
                // Merge all tempos
                var tempos = trackTempos.SelectMany(t => t).ToList();
                
                // Sort tempos and write
                tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                if (tempos.Count == 0)
                    tempos.Add(new TempoEntry { Tick = 0, Value = 500000 });
                
                cacheFs.Position = temposOffset;
                foreach (var t in tempos)
                    WriteTempo(writer, t);
                
                // Update header with final info
                header.TemposCount = tempos.Count;
                header.NoteCount = totalNoteCount;
                header.MidiTime = (uint)maxMidiTime;
                cacheFs.Position = 0;
                WriteHeader(writer, header);
                
                cacheFs.Flush(true);
            }
            finally
            {
                writer?.Dispose();
                cacheFs?.Dispose();
            }

            // === Open cache for rendering (memory map the cache for zero-copy) ===
            Console.WriteLine("内存映射缓存文件用于渲染...");
            
            // Memory-map the cache file for fast rendering (use actual file size)
            long cacheFileSize = new FileInfo(cacheFilePath).Length;
            cacheMmf = MemoryMappedFile.CreateFromFile(cacheFilePath, FileMode.Open, null, cacheFileSize, MemoryMappedFileAccess.Read);
            cacheAccessor = cacheMmf.CreateViewAccessor(0, cacheFileSize, MemoryMappedFileAccess.Read);
            cacheAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref cacheBasePtr);
            
            // Read header from mapped memory
            var readHeader = *(CacheHeader*)cacheBasePtr;
            
            // Validate header
            if (readHeader.Magic != 0x30525351)
                throw new InvalidDataException($"Invalid cache file magic: 0x{readHeader.Magic:X}");
            if (readHeader.Version != 1)
                throw new InvalidDataException($"Unsupported cache version: {readHeader.Version}");
            if (readHeader.Offsets128 != sizeof(CacheHeader))
                throw new InvalidDataException($"Invalid Offsets128: {readHeader.Offsets128}, expected {sizeof(CacheHeader)}");
            
            TrackCount = readHeader.TrackCount;
            Division = readHeader.Division;
            MidiTime = readHeader.MidiTime;
            NoteCount = readHeader.NoteCount;
            TemposCount = readHeader.TemposCount;
            TemposOffset = readHeader.TemposOffset;
            
            Console.WriteLine($"Cache Header: TrackCount={TrackCount}, Division={Division}, MidiTime={MidiTime}, NoteCount={NoteCount}");
            Console.WriteLine($"Offsets128={readHeader.Offsets128}, Counts128={readHeader.Counts128}, DataStart={readHeader.DataStart}");
            
            // Set up note pointers (只使用 0-127 键用于渲染)
            long* offsetsPtr = (long*)(cacheBasePtr + readHeader.Offsets128);
            long* countsPtr = (long*)(cacheBasePtr + readHeader.Counts128);
            
            long cacheFileLength = new FileInfo(cacheFilePath).Length;
            
            for (int k = 0; k < 128; k++)
            {
                KeyDataOffsets[k] = offsetsPtr[k];
                NoteCounts[k] = countsPtr[k];
                
                // Validate offset
                if (KeyDataOffsets[k] < 0 || KeyDataOffsets[k] >= cacheFileLength)
                {
                    Console.WriteLine($"Warning: Key {k} has invalid offset {KeyDataOffsets[k]}, file length {cacheFileLength}");
                    NotePointers[k] = null;
                    continue;
                }
                
                if (NoteCounts[k] > 0)
                {
                    NotePointers[k] = (NoteRef*)(cacheBasePtr + KeyDataOffsets[k]);
                    // Validate data range
                    long dataEnd = KeyDataOffsets[k] + NoteCounts[k] * sizeof(NoteRef);
                    if (dataEnd > cacheFileLength)
                    {
                        Console.WriteLine($"Warning: Key {k} data exceeds file length. Offset={KeyDataOffsets[k]}, Count={NoteCounts[k]}, End={dataEnd}, FileLength={cacheFileLength}");
                        // Clamp the count to valid range
                        NoteCounts[k] = (cacheFileLength - KeyDataOffsets[k]) / sizeof(NoteRef);
                    }
                }
                else
                {
                    NotePointers[k] = null;
                }
            }
            // 128-255 键清零（不渲染）
            for (int k = 128; k < 256; k++)
            {
                KeyDataOffsets[k] = 0;
                NoteCounts[k] = 0;
                NotePointers[k] = null;
            }
            
            // Set up tempo pointer and copy to UnmanagedList
            TemposPtr = (TempoEntry*)(cacheBasePtr + TemposOffset);
            
            // Copy tempos to UnmanagedList for RendererBase compatibility
            Tempos.Clear(); // Prevent accumulation if re-parsed
            var tempoArray = new Tempo[TemposCount];
            for (int i = 0; i < TemposCount; i++)
            {
                tempoArray[i] = new Tempo { Tick = TemposPtr[i].Tick, Value = TemposPtr[i].Value };
            }
            using (var arr = Interoperability.MakeUnmanagedArray(tempoArray))
            {
                Tempos.AddRange(arr);
            }

            Console.WriteLine("Midi 加载完成. 音符总数: {0}.", NoteCount);
        }

        // === Helper methods ===
        private static uint ReadUInt32BE(BinaryReader r) => 
            ((uint)r.ReadByte() << 24) | ((uint)r.ReadByte() << 16) | ((uint)r.ReadByte() << 8) | r.ReadByte();
        
        private static ushort ReadUInt16BE(BinaryReader r) => 
            (ushort)((r.ReadByte() << 8) | r.ReadByte());
        
        private static uint ReadVLQ(BinaryReader r)
        {
            uint val = 0;
            byte b;
            do
            {
                b = r.ReadByte();
                val = (val << 7) | (b & 0x7Fu);
            } while ((b & 0x80) != 0);
            return val;
        }

        private static void WriteHeader(BinaryWriter w, CacheHeader h)
        {
            w.Write(h.Magic);
            w.Write(h.Version);
            w.Write(h.TrackCount);
            w.Write(h.Division);
            w.Write(h.MidiTime);
            w.Write(h.NoteCount);
            w.Write(h.TemposCount);
            w.Write(h.Offsets128);
            w.Write(h.Counts128);
            w.Write(h.TemposOffset);
            w.Write(h.DataStart);
        }

        private static CacheHeader ReadHeader(BinaryReader r) => new()
        {
            Magic = r.ReadInt64(),
            Version = r.ReadInt32(),
            TrackCount = r.ReadUInt16(),
            Division = r.ReadUInt16(),
            MidiTime = r.ReadUInt32(),
            NoteCount = r.ReadInt64(),
            TemposCount = r.ReadInt64(),
            Offsets128 = r.ReadInt64(),
            Counts128 = r.ReadInt64(),
            TemposOffset = r.ReadInt64(),
            DataStart = r.ReadInt64()
        };

        private static void WriteNote(FileStream fs, long position, NoteRef note)
        {
            fs.Position = position;
            byte* ptr = (byte*)&note;
            for (int i = 0; i < sizeof(NoteRef); i++)
                fs.WriteByte(ptr[i]);
        }

        private static void WriteTempo(BinaryWriter w, TempoEntry t)
        {
            w.Write(t.Tick);
            w.Write(t.Value);
        }

        public RenderFile(string path)
        {
            MidiPath = path;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException();
            }
            
            bool success = false;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                Parse();
                success = true;
            }
            catch
            {
                // Ensure cleanup on parse failure
                Dispose();
                throw;
            }
            finally
            {
                sw.Stop();
                if (success)
                    Console.WriteLine("加载 Midi 用时: {0:F2} s.", sw.ElapsedMilliseconds / 1000.0);
            }
        }

        public string MidiPath { get; }

        private bool _disposed = false;
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Release managed resources
                    Tempos?.Dispose();
                    Tempos = null!;
                }
                
                // Release unmanaged resources (always)
                if (cacheAccessor != null)
                {
                    cacheAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    cacheAccessor.Dispose();
                    cacheAccessor = null;
                }
                cacheMmf?.Dispose();
                cacheMmf = null;
                
                // Clear note pointers
                for (int i = 0; i < 256; i++)
                {
                    NotePointers[i] = null;
                }
                cacheBasePtr = null;
                
                // Delete cache file
                if (!string.IsNullOrEmpty(cacheFilePath) && File.Exists(cacheFilePath))
                {
                    try
                    {
                        File.Delete(cacheFilePath);
                        Console.WriteLine($"已删除缓存文件: {cacheFilePath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"删除缓存文件失败: {ex.Message}");
                    }
                }
                
                _disposed = true;
            }
        }

        ~RenderFile()
        {
            Dispose(false);
        }
    }
}
