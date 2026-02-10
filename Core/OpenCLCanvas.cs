using Silk.NET.OpenCL;
using SharpExtension;
using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Runtime.InteropServices;

namespace QQS_UI.Core
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUNoteData
    {
        public int Key;
        public int Y;
        public int Height;
        public uint Color;
        public int ColorIndex;
        public int Pressed;
    }

    public unsafe class OpenCLCanvas : CanvasBase
    {
        private readonly CL cl;
        private readonly OpenCLContext clContext;
        private nint program;
        private nint kernelClear;
        private nint kernelDrawKeys;
        private nint kernelDrawNotes;
        private nint clFrameBuffer, clKeyColors, clKeyPressed, clNoteData, clKeyX, clKeyWidth, clNoteX, clNoteWidth, clDrawMap;
        private readonly uint backgroundColor;
        private readonly bool enableGradient, thinnerNotes, betterBlackKeys, whiteKeyShade, drawSeparator, brighterNotesOnHit;
        private readonly uint separatorColor;
        private readonly double pressedNotesShadeDecrement;
        private double noteRatio, unpressedKeyRatio, pressedKeyRatio, separatorRatio;
        private readonly GPUNoteData[] hostNoteData;
        private int currentNoteCount;
        private bool disposed;

        private const string KernelSource = @"
typedef struct { int key; int y; int height; uint color; int colorIndex; int pressed; } NoteData;
float4 unpack_color(uint c) { return (float4)((float)(c & 0xFF) / 255.0f, (float)((c >> 8) & 0xFF) / 255.0f, (float)((c >> 16) & 0xFF) / 255.0f, (float)((c >> 24) & 0xFF) / 255.0f); }
uint pack_color(float4 c) { return ((uint)(clamp(c.x * 255.0f, 0.0f, 255.0f))) | ((uint)(clamp(c.y * 255.0f, 0.0f, 255.0f)) << 8) | ((uint)(clamp(c.z * 255.0f, 0.0f, 255.0f)) << 16) | ((uint)(clamp(c.w * 255.0f, 0.0f, 255.0f)) << 24); }
uint get_gradient_color(uint startColor, float referenceRatio, int index, int total) {
    if (total <= 1) return startColor;
    float actualRatio = pow(referenceRatio, 1.0f / (float)(total - 1));
    float factor = pow(actualRatio, -(float)index);
    float4 c = unpack_color(startColor);
    return pack_color((float4)(c.xyz * factor, c.w));
}
uint mix_white(uint color, float ratio) { float4 c = unpack_color(color); float4 res = c + (1.0f - c) * ratio; res.w = 1.0f; return pack_color(res); }
__kernel void clearFrame(__global uint* frame, const uint bgColor, const int width, const int height) {
    int gid = get_global_id(0);
    if (gid < width * height) frame[gid] = bgColor;
}
__kernel void drawKeys(__global uint* frame, __global uint* keyColors, __global int* keyPressed, __global int* keyX, __global int* keyWidth, __global int *drawMap, const int keyHeight, const int width, const int height, const int bh, const int bgr, const int dtHeight, const int dtWidth, const int betterBlackKeys, const int whiteKeyShade, const int drawSeparator, const uint separatorColor, const int enableGradient, const float unpressedKeyRatio, const float pressedKeyRatio, const float separatorRatio) {
    int x = get_global_id(0); int y = get_global_id(1);
    if (x >= width || y >= keyHeight + (keyHeight / 15)) return;
    uint color = 0; bool found = false; int diff = keyHeight - bh;
    if (y < keyHeight) {
        for (int i = 75; i < 128; i++) {
            int j = drawMap[i]; int kx = keyX[j]; int kw = keyWidth[j];
            if (betterBlackKeys) {
                if (x >= kx - dtWidth && x < kx + kw + dtWidth && y >= diff - dtWidth && y < diff + bh) {
                    if (keyPressed[j]) color = (y < diff + bh - 2) ? ((x >= kx && x < kx + kw && y >= diff) ? keyColors[j] : 0xFF363636) : 0xFF363636;
                    else color = (x >= kx && x < kx + kw && y >= diff && y < diff + bh + dtHeight) ? 0xFF000000 : 0xFF363636;
                    found = true; break;
                }
            } else if (x >= kx && x < kx + kw && y >= diff && y < diff + bh) {
                color = (x == kx || x == kx + kw || y == diff || y == diff + bh - 1) ? 0xFF000000 : keyColors[j];
                found = true; break;
            }
        }
        if (!found) {
            for (int i = 0; i < 75; i++) {
                int j = drawMap[i]; int kx = keyX[j]; int kw = keyWidth[j];
                if (x >= kx && x < kx + kw && y >= 0 && y < keyHeight) {
                    color = keyColors[j];
                    if (enableGradient) {
                        if (keyPressed[j]) color = get_gradient_color(color, pressedKeyRatio, y, keyHeight);
                        else { int startY = whiteKeyShade ? bgr : 0; if (y >= startY) color = get_gradient_color(0xFFFFFFFF, unpressedKeyRatio, y - startY, keyHeight - startY); }
                    }
                    if (whiteKeyShade && !keyPressed[j]) {
                        if (y >= 1 && y < bgr - 1 && x >= kx + 1 && x < kx + kw) color = 0xFF999999;
                        else if ((y == 0 || y == bgr - 1) && x >= kx && x <= kx + kw) color = 0xFF000000;
                    }
                    if (x == kx || x == kx + kw || y == 0 || y == keyHeight - 1) color = 0xFF000000;
                    found = true; break;
                }
            }
        }
    }
    int sepStart = keyHeight - 2; int sepHeight = keyHeight / 15;
    if (drawSeparator && y >= sepStart && y < sepStart + sepHeight) { color = enableGradient ? get_gradient_color(separatorColor, separatorRatio, y - sepStart, sepHeight) : separatorColor; found = true; }
    if (found) frame[(height - 1 - y) * width + x] = color;
}
__kernel void drawNotes(__global uint* frame, __global NoteData* notes, __global int* noteX, __global int* noteWidth, const int noteCount, const int keyHeight, const int width, const int height, const int enableGradient, const float noteRatio, const int enableNoteBorder, const float noteBorderShade, const int brighterNotesOnHit, const float shadeRatio) {
    int noteIdx = get_global_id(0); if (noteIdx >= noteCount) return;
    NoteData note = notes[noteIdx]; int x0 = noteX[note.key], w = noteWidth[note.key], y0 = note.y, h = note.height;
    uint baseColor = note.color; if (brighterNotesOnHit && note.pressed) baseColor = mix_white(baseColor, shadeRatio);
    int borderWidth = 1, borderHeight = max(keyHeight / 64, 1);
    uint borderColor = pack_color((float4)(unpack_color(baseColor).xyz / noteBorderShade, 1.0f));
    for (int py = y0; py < y0 + h && py < height; py++) {
        if (py < 0) continue;
        for (int px = x0; px < x0 + w && px < width; px++) {
            uint color = baseColor; bool isBorder = false;
            if (enableNoteBorder) {
                if (h > 2 * borderHeight) { if (px < x0 + borderWidth || px >= x0 + w - borderWidth || py < y0 + borderHeight || (py >= y0 + h - borderHeight && py + 1 < height)) { color = borderColor; isBorder = true; } }
                else { color = borderColor; isBorder = true; }
            }
            if (!isBorder && enableGradient) {
                int startX = enableNoteBorder ? x0 + borderWidth : x0; int actualW = enableNoteBorder ? w - 2 * borderWidth : w;
                if (actualW > 1) color = get_gradient_color(baseColor, noteRatio, px - startX, actualW);
            }
            frame[(height - 1 - py) * width + px] = color;
        }
    }
}
";

        public OpenCLCanvas(in RenderOptions options) : base(options)
        {
            cl = OpenCLContext.CLApi;
            backgroundColor = options.TransparentBackground ? (options.BackgroundColor & 0x00FFFFFF) : (uint)options.BackgroundColor;
            enableGradient = options.Gradient; thinnerNotes = options.ThinnerNotes; betterBlackKeys = options.BetterBlackKeys;
            whiteKeyShade = options.WhiteKeyShade; drawSeparator = options.DrawSeparator; separatorColor = options.DivideBarColor;
            brighterNotesOnHit = options.BrighterNotesOnHit; pressedNotesShadeDecrement = options.PressedNotesShadeDecrement / 255.0;
            hostNoteData = new GPUNoteData[20000];
            if (enableGradient) { 
                unpressedKeyRatio = Math.Pow(Global.UnpressedWhiteKeyGradientScale, 154); 
                pressedKeyRatio = Math.Pow(Global.PressedWhiteKeyGradientScale, 162); 
                separatorRatio = Math.Pow(Global.SeparatorGradientScale, 162.0 / 15.0); 
                noteRatio = Math.Pow(Global.NoteGradientScale, 10); 
            }
            clContext = new OpenCLContext(); CompileKernels(); AllocateGPUMemory();
        }

        private void CompileKernels()
        {
            int err; byte[] sourceBytes = Encoding.UTF8.GetBytes(KernelSource);
            fixed (byte* pSource = sourceBytes) { nuint length = (nuint)sourceBytes.Length; byte*[] sourceArray = { pSource }; fixed (byte** ppSource = sourceArray) program = cl.CreateProgramWithSource(clContext.Context, 1, ppSource, &length, &err); }
            nint device = clContext.Device; err = cl.BuildProgram(program, 1, &device, (byte*)null, null, null);
            if (err != 0) { nuint logSize = 0; cl.GetProgramBuildInfo(program, device, ProgramBuildInfo.BuildLog, 0, null, &logSize); byte[] logBytes = new byte[logSize]; fixed (byte* pLog = logBytes) cl.GetProgramBuildInfo(program, device, ProgramBuildInfo.BuildLog, logSize, pLog, null); throw new InvalidOperationException($"OCL Build Error: {Encoding.UTF8.GetString(logBytes)}"); }
            kernelClear = cl.CreateKernel(program, "clearFrame", &err); kernelDrawKeys = cl.CreateKernel(program, "drawKeys", &err); kernelDrawNotes = cl.CreateKernel(program, "drawNotes", &err);
        }

        private void AllocateGPUMemory()
        {
            int err; clFrameBuffer = cl.CreateBuffer(clContext.Context, MemFlags.ReadWrite, (nuint)(width * height * 4), null, &err);
            clKeyColors = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly, (nuint)(128 * 4), null, &err); clKeyPressed = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly, (nuint)(128 * 4), null, &err);
            clNoteData = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly, (nuint)(hostNoteData.Length * sizeof(GPUNoteData)), null, &err); clDrawMap = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly, (nuint)(128 * 4), null, &err);
            fixed (int* pkx = keyx, pkw = keyw, pnx = notex, pnw = notew) {
                clKeyX = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly | MemFlags.CopyHostPtr, (nuint)(128 * 4), pkx, &err); clKeyWidth = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly | MemFlags.CopyHostPtr, (nuint)(128 * 4), pkw, &err);
                clNoteX = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly | MemFlags.CopyHostPtr, (nuint)(128 * 4), pnx, &err); clNoteWidth = cl.CreateBuffer(clContext.Context, MemFlags.ReadOnly | MemFlags.CopyHostPtr, (nuint)(128 * 4), pnw, &err);
            }
            int[] dMap = new int[128]; for (int i = 0; i < 128; i++) dMap[i] = Global.DrawMap[i];
            fixed (int* pMap = dMap) cl.EnqueueWriteBuffer(clContext.CommandQueue, clDrawMap, true, 0, 512, pMap, 0, null, null);
        }

        public override void Clear() {
            base.Clear(); currentNoteCount = 0; int err; nuint pSize = (nuint)sizeof(nint); uint bg = backgroundColor; int w = width, h = height;
            var arg0 = clFrameBuffer;
            err = cl.SetKernelArg(kernelClear, 0, pSize, &arg0); err |= cl.SetKernelArg(kernelClear, 1, 4, &bg); err |= cl.SetKernelArg(kernelClear, 2, 4, &w); err |= cl.SetKernelArg(kernelClear, 3, 4, &h);
            nuint gSize = (nuint)(width * height); cl.EnqueueNdrangeKernel(clContext.CommandQueue, kernelClear, 1, null, &gSize, null, 0, null, null);
        }

        public void AddNoteGPU(int key, int y, int height, uint color, bool pressed, int colorIndex = 0) {
            int idx = System.Threading.Interlocked.Increment(ref currentNoteCount) - 1;
            if (idx < hostNoteData.Length) hostNoteData[idx] = new GPUNoteData { Key = key, Y = y, Height = height, Color = color, ColorIndex = colorIndex, Pressed = pressed ? 1 : 0 };
        }

        public void DrawNotesGPU() {
            if (currentNoteCount == 0) return;
            int err; fixed (GPUNoteData* pData = hostNoteData) cl.EnqueueWriteBuffer(clContext.CommandQueue, clNoteData, true, 0, (nuint)(currentNoteCount * sizeof(GPUNoteData)), pData, 0, null, null);
            nuint pSize = (nuint)sizeof(nint); int count = currentNoteCount, kh = keyh, w = width, h = height, bg = enableGradient ? 1 : 0, bnb = Global.EnableNoteBorder ? 1 : 0, bbh = brighterNotesOnHit ? 1 : 0;
            float nr = (float)noteRatio, nbs = (float)Global.NoteBorderShade, sr = (float)pressedNotesShadeDecrement;
            var arg0 = clFrameBuffer; var arg1 = clNoteData; var arg2 = clNoteX; var arg3 = clNoteWidth;
            err = cl.SetKernelArg(kernelDrawNotes, 0, pSize, &arg0); err |= cl.SetKernelArg(kernelDrawNotes, 1, pSize, &arg1); err |= cl.SetKernelArg(kernelDrawNotes, 2, pSize, &arg2); err |= cl.SetKernelArg(kernelDrawNotes, 3, pSize, &arg3);
            err |= cl.SetKernelArg(kernelDrawNotes, 4, 4, &count); err |= cl.SetKernelArg(kernelDrawNotes, 5, 4, &kh); err |= cl.SetKernelArg(kernelDrawNotes, 6, 4, &w); err |= cl.SetKernelArg(kernelDrawNotes, 7, 4, &h);
            err |= cl.SetKernelArg(kernelDrawNotes, 8, 4, &bg); err |= cl.SetKernelArg(kernelDrawNotes, 9, 4, &nr); err |= cl.SetKernelArg(kernelDrawNotes, 10, 4, &bnb); err |= cl.SetKernelArg(kernelDrawNotes, 11, 4, &nbs); err |= cl.SetKernelArg(kernelDrawNotes, 12, 4, &bbh); err |= cl.SetKernelArg(kernelDrawNotes, 13, 4, &sr);
            nuint gSize = (nuint)count; cl.EnqueueNdrangeKernel(clContext.CommandQueue, kernelDrawNotes, 1, null, &gSize, null, 0, null, null);
        }

        public void DrawKeysGPU() {
            if (keyh == 0) return;
            int err; fixed (uint* pColors = KeyColors) cl.EnqueueWriteBuffer(clContext.CommandQueue, clKeyColors, true, 0, 512, pColors, 0, null, null);
            fixed (bool* pPressed = KeyPressed) {
                int[] pInts = new int[128]; for (int i = 0; i < 128; i++) pInts[i] = pPressed[i] ? 1 : 0;
                fixed (int* pPI = pInts) cl.EnqueueWriteBuffer(clContext.CommandQueue, clKeyPressed, true, 0, 512, pPI, 0, null, null);
            }
            int bh = (whiteKeyShade || betterBlackKeys) ? keyh * 64 / 100 : keyh * 66 / 100, bgr = keyh / 20, dth = (int)Math.Round(keyh / 45.0), dtw = (int)Math.Round(width / 1500.0);
            int bbk = betterBlackKeys ? 1 : 0, wks = whiteKeyShade ? 1 : 0, ds = drawSeparator ? 1 : 0, eg = enableGradient ? 1 : 0;
            uint sc = separatorColor; float ukr = (float)unpressedKeyRatio, pkr = (float)pressedKeyRatio, spr = (float)separatorRatio;
            nuint pSize = (nuint)sizeof(nint); int kh = keyh, w = width, h = height;
            var arg0 = clFrameBuffer; var arg1 = clKeyColors; var arg2 = clKeyPressed; var arg3 = clKeyX; var arg4 = clKeyWidth; var arg5 = clDrawMap;
            err = cl.SetKernelArg(kernelDrawKeys, 0, pSize, &arg0); err |= cl.SetKernelArg(kernelDrawKeys, 1, pSize, &arg1); err |= cl.SetKernelArg(kernelDrawKeys, 2, pSize, &arg2); err |= cl.SetKernelArg(kernelDrawKeys, 3, pSize, &arg3); err |= cl.SetKernelArg(kernelDrawKeys, 4, pSize, &arg4); err |= cl.SetKernelArg(kernelDrawKeys, 5, pSize, &arg5);
            err |= cl.SetKernelArg(kernelDrawKeys, 6, 4, &kh); err |= cl.SetKernelArg(kernelDrawKeys, 7, 4, &w); err |= cl.SetKernelArg(kernelDrawKeys, 8, 4, &h); err |= cl.SetKernelArg(kernelDrawKeys, 9, 4, &bh); err |= cl.SetKernelArg(kernelDrawKeys, 10, 4, &bgr); err |= cl.SetKernelArg(kernelDrawKeys, 11, 4, &dth); err |= cl.SetKernelArg(kernelDrawKeys, 12, 4, &dtw); err |= cl.SetKernelArg(kernelDrawKeys, 13, 4, &bbk); err |= cl.SetKernelArg(kernelDrawKeys, 14, 4, &wks); err |= cl.SetKernelArg(kernelDrawKeys, 15, 4, &ds); err |= cl.SetKernelArg(kernelDrawKeys, 16, 4, &sc); err |= cl.SetKernelArg(kernelDrawKeys, 17, 4, &eg); err |= cl.SetKernelArg(kernelDrawKeys, 18, 4, &ukr); err |= cl.SetKernelArg(kernelDrawKeys, 19, 4, &pkr); err |= cl.SetKernelArg(kernelDrawKeys, 20, 4, &spr);
            nuint* gSize = stackalloc nuint[2] { (nuint)width, (nuint)(keyh + (keyh / 15)) }; cl.EnqueueNdrangeKernel(clContext.CommandQueue, kernelDrawKeys, 2, null, gSize, null, 0, null, null);
        }

        public override void WriteFrame() { cl.EnqueueReadBuffer(clContext.CommandQueue, clFrameBuffer, true, 0, (nuint)(width * height * 4), frame, 0, null, null); base.WriteFrame(); }
        public override void DrawKeys() => DrawKeysGPU();
        public override void DrawGradientKeys() => DrawKeysGPU();
        public override void DrawNote(short key, int track, int y, int height, uint color, bool pressed) => AddNoteGPU(key, y, height, color, pressed, track);
        public override void DrawGradientNote(short key, int track, int y, int height, bool pressed) => AddNoteGPU(key, y, height, Global.NoteColors[track % Global.KeyColors.Length], pressed, track);
        public override void Dispose() {
            if (!disposed) {
                if (kernelClear != 0) cl.ReleaseKernel(kernelClear); if (kernelDrawKeys != 0) cl.ReleaseKernel(kernelDrawKeys); if (kernelDrawNotes != 0) cl.ReleaseKernel(kernelDrawNotes);
                if (program != 0) cl.ReleaseProgram(program); if (clFrameBuffer != 0) cl.ReleaseMemObject(clFrameBuffer);
                if (clKeyColors != 0) cl.ReleaseMemObject(clKeyColors); if (clKeyPressed != 0) cl.ReleaseMemObject(clKeyPressed);
                if (clNoteData != 0) cl.ReleaseMemObject(clNoteData); if (clKeyX != 0) cl.ReleaseMemObject(clKeyX);
                if (clKeyWidth != 0) cl.ReleaseMemObject(clKeyWidth); if (clNoteX != 0) cl.ReleaseMemObject(clNoteX);
                if (clNoteWidth != 0) cl.ReleaseMemObject(clNoteWidth); if (clDrawMap != 0) cl.ReleaseMemObject(clDrawMap);
                clContext?.Dispose(); base.Dispose(); disposed = true;
            }
        }
        ~OpenCLCanvas() => Dispose();
    }
}
