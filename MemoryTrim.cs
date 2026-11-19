using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace LunchBox;

/// <summary>
/// 常駐アプリ向けのメモリ整理。履歴収集などで出た一時データを回収し、使っていないページを OS に返す。
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static long _lastAllocated;

    public static void Run(string reason)
    {
        try
        {
            long before = GC.GetTotalMemory(false);
            // 前回の整理からの間に作った一時データの総量（途中で回収された分も含む）。待機中の無駄の目安
            long allocated = GC.GetTotalAllocatedBytes(false);
            long sinceLast = allocated - _lastAllocated;
            _lastAllocated = allocated;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            using var p = Process.GetCurrentProcess();
            long workingSet = p.WorkingSet64;
            EmptyWorkingSet(p.Handle);
            p.Refresh();
            Logger.Log($"メモリ整理({reason}): 管理ヒープ {before / 1048576}MB→{GC.GetTotalMemory(false) / 1048576}MB / プライベート {p.PrivateMemorySize64 / 1048576}MB / ハンドル {p.HandleCount}"
                       + $" / 前回からの割り当て {sinceLast / 1048576.0:F1}MB / 整理前の使用中 {workingSet / 1048576}MB");
        }
        catch (Exception ex)
        {
            Logger.Log("メモリ整理失敗: " + ex.Message);
        }
    }
}
