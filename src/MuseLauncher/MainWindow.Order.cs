using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MuseLauncher.Core;

namespace MuseLauncher;

public partial class MainWindow
{
    private const string AccountOrderFormat = "MuseLauncher.AccountOrder";
    private Point? accountDragStart;
    private long? draggingAccountId;
    private bool suppressAccountClick;

    private void AttachAccountOrderHandlers(Border row, Account account)
    {
        row.AllowDrop = true;
        row.ToolTip = "按住头像或账号名称上下拖动，调整顺序";
        var normalBrush = row.BorderBrush;
        var normalThickness = row.BorderThickness;
        void ResetMarker()
        {
            row.BorderBrush = normalBrush;
            row.BorderThickness = normalThickness;
        }
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            suppressAccountClick = false;
            accountDragStart = !credentialTransferBusy && !closing && editId < 0 && confirmDelId < 0
                && !IsButtonSource(e.OriginalSource as DependencyObject) ? e.GetPosition(row) : null;
        };
        row.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || accountDragStart is not Point start
                || draggingAccountId != null || credentialTransferBusy || closing) return;
            var position = e.GetPosition(row);
            if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            accountDragStart = null;
            draggingAccountId = account.Id;
            suppressAccountClick = true;
            try
            {
                DragDrop.DoDragDrop(row, new DataObject(AccountOrderFormat, account.Id), DragDropEffects.Move);
            }
            finally
            {
                draggingAccountId = null;
                ResetMarker();
                RenderAccounts();
            }
            e.Handled = true;
        };
        row.DragOver += (_, e) =>
        {
            if (!CanMoveAccount(e.Data, account.Id)) { e.Effects = DragDropEffects.None; return; }
            var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
            row.BorderBrush = AccentBrush;
            row.BorderThickness = after ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
            var position = e.GetPosition(AccountScroller);
            if (position.Y < 28) AccountScroller.ScrollToVerticalOffset(AccountScroller.VerticalOffset - 12);
            else if (position.Y > AccountScroller.ActualHeight - 28)
                AccountScroller.ScrollToVerticalOffset(AccountScroller.VerticalOffset + 12);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        row.DragLeave += (_, _) => ResetMarker();
        row.Drop += (_, e) =>
        {
            try
            {
                if (!CanMoveAccount(e.Data, account.Id)) { e.Effects = DragDropEffects.None; return; }
                var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
                MoveAccount(draggingAccountId!.Value, account.Id, after);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
            finally { ResetMarker(); }
        };
    }

    private bool CanMoveAccount(IDataObject data, long targetId)
        => !closing && !credentialTransferBusy && changingAccounts.Count == 0
            && draggingAccountId is long sourceId && sourceId != targetId
            && data.GetDataPresent(AccountOrderFormat) && data.GetData(AccountOrderFormat) is long value
            && value == sourceId;

    internal void MoveAccount(long id, long targetId, bool after)
    {
        if (closing || credentialTransferBusy || changingAccounts.Count > 0) return;
        try
        {
            store.Move(id, targetId, after);
            RenderAccounts();
        }
        catch (Exception ex) { ReportOperationFailure("账号顺序未保存", ex); }
    }
}
