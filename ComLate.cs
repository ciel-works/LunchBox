using System.Reflection;
using System.Runtime.InteropServices;

namespace LunchBox;

/// <summary>
/// COM オブジェクト（Shell.Application・WScript.Shell）を名前で呼ぶための小さな道具。
/// C# の dynamic は、使った時点で Microsoft.CSharp と System.Linq.Expressions（式の組み立てとコンパイル）を
/// 読み込んで呼び出しごとのキャッシュを持つ。常駐中に 30 秒ごとに使う処理があるので、それを避けて直接呼ぶ（11/17）。
/// </summary>
internal static class ComLate
{
    private const BindingFlags GetFlags = BindingFlags.GetProperty | BindingFlags.InvokeMethod;

    /// <summary>ProgID からオブジェクトを作る（無ければ null）</summary>
    public static object? Create(string progId)
    {
        Type? t = Type.GetTypeFromProgID(progId);
        return t is null ? null : Activator.CreateInstance(t);
    }

    /// <summary>プロパティの取得・引数なしのメソッド呼び出し</summary>
    public static object? Get(object target, string name)
        => target.GetType().InvokeMember(name, GetFlags, null, target, null);

    /// <summary>引数ありのメソッド呼び出し</summary>
    public static object? Call(object target, string name, params object[] args)
        => target.GetType().InvokeMember(name, GetFlags, null, target, args);

    public static string GetString(object target, string name) => Get(target, name) as string ?? "";

    /// <summary>COM オブジェクトなら参照を放す（それ以外・null は何もしない）</summary>
    public static void Release(object? o)
    {
        try { if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
    }
}
