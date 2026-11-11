using System;
using System.Collections.Generic;

namespace AnoMech.Core.Game;

// SimEvents 的佇列本體，單獨成檔以便 Tests 以 Compile Include 連結（SimEvents 的事件型別
// 帶 SimCharacter，連不進測試專案）。語意照上游 SimEvents：沒有 Listener 時發生的事件直接丟棄；
// 事件只在 Dispatch 時送出；hook 在送出途中再發的事件同一輪送完；某個 hook 擲例外只略過該筆。
internal sealed class DeferredDispatch<TListener> where TListener : class
{
    private readonly Queue<Action<TListener>> pending = new();

    internal TListener? Listener { get; set; }

    internal void Enqueue(Action<TListener> deliver)
    {
        if (Listener is not null) pending.Enqueue(deliver);
    }

    // onHookError 收到被略過的例外；之後的事件照常送出。
    internal void Dispatch(Action<Exception> onHookError)
    {
        while (Listener is { } listener && pending.TryDequeue(out var deliver))
        {
            try
            {
                deliver(listener);
            }
            catch (Exception e)
            {
                onHookError(e);
            }
        }
    }

    internal void Clear()
    {
        Listener = null;
        pending.Clear();
    }
}
