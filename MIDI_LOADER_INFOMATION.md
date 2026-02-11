## 当前MIDI解析逻辑

### **两遍扫描架构**

#### **第一遍：统计分析（只读扫描）**
```
目标：计算每个键的音符数量、最大MIDI时间
方式：并行扫描所有轨道
```

**流程**：
1. 并行打开每个轨道（独立`FileStream`）
2. 使用`stackalloc long[256]`在栈上统计256个键的音符数
3. 处理Running Status（`comm < 0x80`时回退1字节，复用上次命令）
4. 只统计Note On（velocity≠0）事件
5. 用`Interlocked`原子操作累加全局计数器
6. 计算文件布局：每个键的偏移量和大小

---

#### **第二遍：解析写入（流式处理）**

**初始化**：
- 创建1MB缓冲的`FileStream`（顺序写入优化）
- 预分配整个缓存文件（避免磁盘碎片）
- 写入占位符Header + 256个键的偏移/数量表

**并行轨道解析**（核心优化点）：
```csharp
Parallel.For(0, TrackCount, trackIdx => {
    // 1. 每个线程独立的数据结构
    var trackNotes = new List<NoteRef>[256];  // 线程本地缓冲
    var pendingPos/Start[256];                // 未结束音符栈
    var localTempos;                          // 本轨道的Tempo事件

    // 2. 解析轨道内所有MIDI事件
    while (未到轨道末尾) {
        trkTime += ReadVLQ();  // 累积时间戳

        处理Running Status（复用上次命令字节）

        switch (命令类型) {
            Note On (0x90):
                if (velocity != 0)
                    pending栈.Push(起始时间)
                else
                    创建音符并加入trackNotes

            Note Off (0x80):
                Pop pending栈，创建音符并加入trackNotes

            Track End (0xFF 0x2F):
                强制结束所有pending音符

            Tempo (0xFF 0x51):
                解析Tempo值并记录

            其他事件:
                跳过数据
        }
    }

    // 3. 立即写入并释放内存
    for (每个键 k in 0..255) {
        if (trackNotes[k].Count == 0) continue;

        trackNotes[k].Sort(按Start排序);  // 轨道内排序

        lock (keyWriteLocks[k]) {
            定位到键k的写入位置;
            写入所有音符（BinaryWriter，12字节/音符）;
            更新keyWrittenCounts[k];
        }

        trackNotes[k].Clear();  // 立即释放
    }

    trackNotes = null;  // 帮助GC

    // 4. 每10个轨道触发GC
    if (completed % 10 == 0)
        GC.Collect(2, Forced);
});
```

---

#### **最终排序阶段**（多轨道音符合并）
```csharp
Parallel.For(0, 256, k => {
    // 1. 读取该键的全部音符
    buffer = new byte[noteCountsPerKey[k] * 12];
    lock (keyWriteLocks[k]) {
        cacheFs.Read(buffer);  // 批量读取
    }

    // 2. 锁外解析（减少持锁时间）
    for (每个音符) {
        用BitConverter解析字节 → NoteRef结构
    }

    // 3. 全局排序（合并多轨道）
    Array.Sort(notes, 按Start排序);

    // 4. 转换回字节并写回
    for (每个音符) {
        用BitConverter.TryWriteBytes写入buffer
    }
    lock (keyWriteLocks[k]) {
        cacheFs.Write(buffer);  // 批量写回
    }
});
```

---

### **关键优化技术**

**内存管理**：
- ✅ 线程本地`trackNotes[256]`（避免全局共享）
- ✅ 轨道解析完立即写入+释放（流式处理）
- ✅ 定期强制GC（每10轨道）
- ✅ 预估容量`List(128)`（减少扩容）

**并发安全**：
- ✅ 256个独立锁`keyWriteLocks[k]`（降低锁竞争）
- ✅ 原子计数器`Interlocked.Add`
- ✅ 批量字节读写（避免`BinaryReader/Writer`缓冲区冲突）

**Running Status处理**：
```csharp
if (comm < 0x80) {
    fs.Seek(-1, SeekOrigin.Current);  // 回退数据字节
    comm = prev;                       // 复用上次命令
}
```

**数据结构**：
- `NoteRef`：12字节（Start, End, Track, Key, padding）
- `ForwardLinkedList`：追踪未结束音符（支持嵌套发声）

**预期效果**：峰值内存~500MB（vs 旧版4GB），完全释放无残留。