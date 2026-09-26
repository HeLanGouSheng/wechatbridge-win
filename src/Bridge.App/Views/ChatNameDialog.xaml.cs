using System.Windows;
using Bridge.Core.Delivery;

namespace Bridge.App.Views;

/// <summary>The export carries no group name, so the first time a set of people shows up we ask once.</summary>
public partial class ChatNameDialog : Window
{
    public ChatNameDialog(ChatNameRequest request)
    {
        InitializeComponent();
        var people = request.Senders.Count > 6
            ? string.Join("、", request.Senders.Take(6)) + $" 等 {request.Senders.Count} 人"
            : string.Join("、", request.Senders);
        Intro.Text = $"这段记录有 {request.MessageCount} 条消息，{people} 在里面发言。微信导出的记录里没有群名，请填一下，llmsocial 里会按这个名字建对话。";
        NameBox.Focus();
    }

    public ChatNameAnswer? Answer { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        Answer = new ChatNameAnswer(name, Remember.IsChecked == true);
        DialogResult = true;
    }

    public static Task<ChatNameAnswer?> AskAsync(Window? owner, ChatNameRequest request)
    {
        var dialog = new ChatNameDialog(request);
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
        return Task.FromResult(dialog.Answer);
    }
}
