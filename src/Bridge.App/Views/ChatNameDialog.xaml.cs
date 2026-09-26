using System.Windows;
using Bridge.Core.Delivery;

namespace Bridge.App.Views;

/// <summary>
/// The export carries neither the user's own name nor a group name. Two unknown senders: ask which one
/// is the user (a 1:1 chat is the common case) before treating it as a group; more than two: ask the
/// group's name. Both answers are remembered, so each question is asked once.
/// </summary>
public partial class ChatNameDialog : Window
{
    private readonly ChatNameRequest _request;

    public ChatNameDialog(ChatNameRequest request)
    {
        _request = request;
        InitializeComponent();
        var people = request.Senders.Count > 6
            ? string.Join("、", request.Senders.Take(6)) + $" 等 {request.Senders.Count} 人"
            : string.Join("、", request.Senders);

        if (request.MyNameUnknown && request.Senders.Count == 2)
        {
            Title = "这是谁的聊天？";
            Intro.Text = $"这段记录有 {request.MessageCount} 条消息，发言的是「{request.Senders[0]}」和「{request.Senders[1]}」。微信导出的记录里没写哪个是你。";
            FirstIsMe.Content = $"「{request.Senders[0]}」是我";
            SecondIsMe.Content = $"「{request.Senders[1]}」是我";
            WhoPanel.Visibility = Visibility.Visible;
            WhoFooter.Visibility = Visibility.Visible;
            GroupPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "这是哪个群？";
            Intro.Text = $"这段记录有 {request.MessageCount} 条消息，{people} 在里面发言。微信导出的记录里没有群名，请填一下，llmsocial 里会按这个名字建对话。";
            NameBox.Focus();
        }
    }

    public ChatNameAnswer? Answer { get; private set; }

    private void FirstIsMe_Click(object sender, RoutedEventArgs e) => PickMe(_request.Senders[0]);

    private void SecondIsMe_Click(object sender, RoutedEventArgs e) => PickMe(_request.Senders[1]);

    private void PickMe(string name)
    {
        Answer = new ChatNameAnswer(null, false, name);
        DialogResult = true;
    }

    private void IsGroup_Click(object sender, RoutedEventArgs e)
    {
        Title = "这是哪个群？";
        Intro.Text = "请填群名，llmsocial 里会按这个名字建对话。";
        WhoPanel.Visibility = Visibility.Collapsed;
        WhoFooter.Visibility = Visibility.Collapsed;
        GroupPanel.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

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
