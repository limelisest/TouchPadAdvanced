using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

// A selection owns only the modifier keys it injected. Task selection stays inside
// the native Alt+Tab UI until lift; desktop selection follows displacement updates.
internal sealed class GestureActionExecutor : IDisposable
{
    private enum SelectionKind { None, Tasks, Desktops }
    private readonly Func<Native.Input[], uint> send;
    private readonly Func<int, bool> isHeld;
    private readonly Action<GestureInvocation> invoke;
    private readonly List<ushort> ownedKeys = [];
    private SelectionKind kind;
    private int offset;
    internal bool IsActive => kind != SelectionKind.None;
    internal int OwnedKeyCount => ownedKeys.Count;

    internal GestureActionExecutor(Func<Native.Input[], uint>? send = null,
        Func<int, bool>? isHeld = null, Action<GestureInvocation>? invoke = null)
    {
        this.send = send ?? (inputs => Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()));
        this.isHeld = isHeld ?? (key => (Native.GetAsyncKeyState(key) & 0x8000) != 0);
        this.invoke = invoke ?? GestureActions.Execute;
    }

    internal void Execute(GestureInvocation invocation)
    {
        switch (invocation.Interaction)
        {
            case GestureInteraction.Invoke:
                Cancel();
                invoke(invocation);
                return;
            case GestureInteraction.Begin:
                Cancel();
                ValidateModifiers();
                kind = Kind(invocation.Binding.Action);
                if (kind == SelectionKind.None || invocation.SelectionOffset == 0)
                {
                    kind = SelectionKind.None;
                    throw new InvalidOperationException("无效的连续选择动作。");
                }
                if (kind == SelectionKind.Tasks) Send([GestureActions.Key(0x12, false)]);
                MoveTo(invocation.SelectionOffset);
                return;
            case GestureInteraction.Update:
                if (kind == SelectionKind.None || Kind(invocation.Binding.Action) != kind)
                    throw new InvalidOperationException("连续选择尚未开始。");
                ValidateModifiers();
                MoveTo(invocation.SelectionOffset);
                return;
            case GestureInteraction.Commit:
                ReleaseOwned();
                kind = SelectionKind.None;
                offset = 0;
                return;
            case GestureInteraction.Cancel:
                Cancel();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(invocation));
        }
    }

    private static SelectionKind Kind(GestureAction action) =>
        GestureCatalog.SelectsTask(action) ? SelectionKind.Tasks :
        action is GestureAction.PreviousDesktop or GestureAction.NextDesktop ? SelectionKind.Desktops : SelectionKind.None;

    private void ValidateModifiers()
    {
        foreach (ushort key in new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if (!ownedKeys.Contains(key) && isHeld(key))
                throw new InvalidOperationException("请先松开修饰键，再使用连续切换手势。");
        if (kind == SelectionKind.None && isHeld(0x09)) throw new InvalidOperationException("请先松开 Tab 键。");
    }

    private void MoveTo(int target)
    {
        int delta = target - offset;
        if (delta == 0) return;
        var inputs = new List<Native.Input>();
        if (kind == SelectionKind.Tasks)
        {
            if (delta < 0) inputs.Add(GestureActions.Key(0x10, false));
            for (int i = 0; i < Math.Abs(delta); i++)
            {
                inputs.Add(GestureActions.Key(0x09, false));
                inputs.Add(GestureActions.Key(0x09, true));
            }
            if (delta < 0) inputs.Add(GestureActions.Key(0x10, true));
        }
        else
        {
            var action = delta > 0 ? GestureAction.NextDesktop : GestureAction.PreviousDesktop;
            for (int i = 0; i < Math.Abs(delta); i++)
                inputs.AddRange(GestureActions.Build(new GestureBinding(GestureId.TopSlideRight, action)));
        }
        Send(inputs.ToArray());
        offset = target;
    }

    private uint SendAndTrack(Native.Input[] inputs)
    {
        uint sent = send(inputs);
        foreach (var input in inputs.Take((int)sent))
        {
            if (input.Type != 1) continue;
            ushort key = input.Keyboard.VirtualKey;
            if ((input.Keyboard.Flags & 2) == 0)
            {
                if (!ownedKeys.Contains(key)) ownedKeys.Add(key);
            }
            else ownedKeys.Remove(key);
        }
        return sent;
    }

    private void Send(Native.Input[] inputs)
    {
        if (inputs.Length == 0) return;
        uint sent = SendAndTrack(inputs);
        if (sent == inputs.Length) return;
        int error = Marshal.GetLastWin32Error();
        if (kind == SelectionKind.Tasks) DismissTask(throwOnFailure: false);
        ReleaseOwned(throwOnFailure: false);
        kind = SelectionKind.None;
        offset = 0;
        throw new InvalidOperationException($"切换输入未完整发送：{sent}/{inputs.Length}，错误码 {error}。");
    }

    private void ReleaseOwned(bool throwOnFailure = true)
    {
        var releases = ownedKeys.AsEnumerable().Reverse().Select(key => GestureActions.Key(key, true)).ToArray();
        if (releases.Length == 0) return;
        uint sent = SendAndTrack(releases);
        if (sent != releases.Length && throwOnFailure)
            throw new InvalidOperationException("未能释放切换手势的按键。");
    }

    private void DismissTask(bool throwOnFailure)
    {
        // Shift/Tab may still be held after a partial reverse-selection batch.
        // Release them before Escape so cancellation cannot become Alt+Shift+Esc.
        var transient = ownedKeys.Where(key => key != 0x12).Reverse()
            .Select(key => GestureActions.Key(key, true)).ToArray();
        bool complete = transient.Length == 0 || SendAndTrack(transient) == transient.Length;
        if (ownedKeys.Contains(0x12))
        {
            Native.Input[] escape = [GestureActions.Key(0x1B, false), GestureActions.Key(0x1B, true)];
            complete &= SendAndTrack(escape) == escape.Length;
        }
        if (!complete && throwOnFailure) throw new InvalidOperationException("未能完整取消任务选择。");
    }
    internal void Cancel()
    {
        try
        {
            if (kind == SelectionKind.Tasks) DismissTask(throwOnFailure: true);
        }
        finally
        {
            kind = SelectionKind.None;
            offset = 0;
            ReleaseOwned();
        }
    }

    public void Dispose() => Cancel();
}
