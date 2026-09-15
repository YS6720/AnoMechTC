// net9 相容層：`Enumerable.Shuffle` 是 .NET 10 才加入的，台服 Dalamud 13 走 net9。
//
// 刻意放在 System.Linq 命名空間 —— 呼叫端既有的 `using System.Linq;` 就能接上，
// 16 處呼叫一行都不用改。日後若升 net10，刪掉本檔即可（屆時會與 BCL 版本衝突，
// 編譯器會直接指出來，不會靜默走錯版本）。

using System.Collections.Generic;

namespace System.Linq;

internal static class EnumerableShuffleCompat
{
    /// <summary>Fisher-Yates，回傳打亂後的新序列（不動原集合）。</summary>
    public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = source.ToArray();
        for (var i = buffer.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }
        return buffer;
    }
}
