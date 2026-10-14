using System.Windows.Input;

namespace AnimaAIProxy.Core;

/// <summary>
/// 把一个无参动作包成 <see cref="ICommand"/>：托盘菜单项的唯一接线方式。
///
/// **为什么菜单项一定要用 Command 而不是 Click。**
/// H.NotifyIcon 在 WinUI 下的默认 <c>ContextMenuMode</c> 是 <c>PopupMenu</c>——
/// 它不渲染我们的 <c>MenuFlyout</c>，而是照着它建一个 **Win32 弹出菜单**，
/// 点击时调用每个 <c>MenuFlyoutItem</c> 上的 <c>Command</c>（库自己的 README
/// 就是这么写的："will call the commands attached to your MenuFlyoutItem"）。
/// 绑 <c>Click</c> 的话菜单照常弹出、每一项都能点，但**没有一行代码会执行**：
/// 实测点「停止代理」之后服务照旧运行，日志里连一条记录都没有。
/// 这类"看着完全正常、实际完全没接上"的接线错误，光看界面是发现不了的。
///
/// **刻意不实现 <c>CanExecute</c> 的实时求值。** Win32 菜单是在弹出时一次性
/// 建好的，<c>CanExecute</c> 若被求值一次就会被固化成灰项——"启动代理"在
/// 代理已经起来之后仍然灰着，而用户没有任何办法让它刷新。宁可让两项始终可点、
/// 由动作自己判（<c>if (!IsRunning)</c> 那两句），也不要一个会卡住状态的灰项。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) =>
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    /// <summary>
    /// 永远返回 true，理由见类注释。这不是占位实现，是一个经过取舍的决定。
    /// </summary>
    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();

    /// <summary>
    /// 从不触发。XAML 要求实现这个事件；我们没有会变的可执行状态，
    /// 显式写成空实现而不是丢一个永远不用的字段。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
}
