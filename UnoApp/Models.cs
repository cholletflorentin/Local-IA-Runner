using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalIARunner.Uno;

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content = "";

    public string Role { get; set; } = "user";
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Conversation : INotifyPropertyChanged
{
    private string _title = "Nouvelle discussion";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged();
        }
    }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> History { get; set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AppState
{
    public string Endpoint { get; set; } = "http://127.0.0.1:8080";
    public List<Conversation> Conversations { get; set; } = [];
}
