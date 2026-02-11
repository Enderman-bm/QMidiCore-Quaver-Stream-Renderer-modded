using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using QQS_UI.Core;

namespace QQS_UI.Tests
{
    [TestFixture]
    public class RenderFilePerformanceTests
    {
        // 用户指定的测试 MIDI 路径
        private const string TestMidiPath = @"D:\BM-DATA\MIDI File\Rekt Apple!!.mid";

        [Test]
        public void RenderFile_Load_MemoryAndTime_ShouldMeetConstraints()
        {
            if (!File.Exists(TestMidiPath))
            {
                Assert.Inconclusive($"测试 MIDI 文件不存在: {TestMidiPath}");
            }

            // 预热一次 GC，尽量减少噪声
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var process = Process.GetCurrentProcess();

            long beforeBytes = process.WorkingSet64;
            var sw = Stopwatch.StartNew();

            using (var renderFile = new RenderFile(TestMidiPath))
            {
                sw.Stop();

                process.Refresh();
                long afterBytes = process.WorkingSet64;
                long deltaBytes = Math.Max(0, afterBytes - beforeBytes);

                long elapsedMs = sw.ElapsedMilliseconds;
                double deltaMB = deltaBytes / (1024.0 * 1024.0);

                string msg = $"RenderFile.Load: elapsed={elapsedMs} ms, deltaMem={deltaMB:F2} MB";
                TestContext.WriteLine(msg);
                Console.WriteLine(msg);

                Assert.LessOrEqual(elapsedMs, 3000, $"MIDI 加载耗时超过 3000 ms，实际 {elapsedMs} ms");
                Assert.LessOrEqual(deltaMB, 30.0, $"MIDI 加载额外内存占用超过 30 MB，实际 {deltaMB:F2} MB");
            }
        }
    }
}

