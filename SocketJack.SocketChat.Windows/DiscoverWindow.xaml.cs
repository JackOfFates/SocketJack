using SocketJack.SocketChat;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace SocketJack.SocketChat.Windows;

public partial class DiscoverWindow : Window
{
    private readonly SocketChatUserProfile _profile;
    private readonly ProfileStore _profileStore;
    private readonly SocketChatDeviceIdentity _identity;
    private SocketChatMasterList _masterList = new();
    private readonly ObservableCollection<PersonDirectoryItem> _people = new();
    private readonly ObservableCollection<PersonDirectoryItem> _friends = new();
    private readonly ObservableCollection<PersonDirectoryItem> _requests = new();
    private readonly ObservableCollection<ServerDirectoryItem> _servers = new();
    public event Action<SocketChatDirectoryServer>? ServerJoined;

    public DiscoverWindow(SocketChatUserProfile profile, ProfileStore profileStore, SocketChatDeviceIdentity identity)
    {
        InitializeComponent();
        _profile = profile; _profileStore = profileStore; _identity = identity;
        PeopleList.ItemsSource = _people; FriendsList.ItemsSource = _friends; RequestsList.ItemsSource = _requests; ServersList.ItemsSource = _servers;
        RefreshDirectory();
    }

    private SocketChatCoordinatorService Service => new SocketChatCoordinatorService(_profile.CoordinatorUrl);

    private void RefreshDirectory()
    {
        try
        {
            PublishSelf();
            _masterList = Service.Read();
            foreach (var accepted in _masterList.FriendRequests.Where(r => r.FromFingerprint == _identity.Fingerprint && r.AcceptedUtc.HasValue && IsValidAcceptedRequest(r)))
                if (!_profile.FriendFingerprints.Contains(accepted.ToFingerprint, StringComparer.Ordinal)) _profile.FriendFingerprints.Add(accepted.ToFingerprint);
            _profileStore.Save(_profile);
            RenderLists();
            MasterListStatusText.Text = _profile.CoordinatorUrl + " · revision " + _masterList.Revision;
        }
        catch (Exception ex) { MasterListStatusText.Text = "Masterlist unavailable: " + ex.Message; }
    }

    private void PublishSelf()
    {
        Service.Update(list =>
        {
            string userValue = UserSigningValue(_identity.Fingerprint, _profile.Username, _profile.BanIdentityKey);
            var user = new SocketChatDirectoryUser { Fingerprint = _identity.Fingerprint, PublicKey = _identity.PublicKey, Username = _profile.Username, BanIdentityKey = _profile.BanIdentityKey, LastSeenUtc = DateTimeOffset.UtcNow, Signature = _identity.Sign(userValue) };
            list.Users.RemoveAll(item => item.Fingerprint == user.Fingerprint); list.Users.Add(user);
            var server = new SocketChatDirectoryServer { Id = "local-" + _identity.Fingerprint, Name = _profile.Username + "'s Lobby", HostFingerprint = _identity.Fingerprint, Endpoint = _profile.CoordinatorUrl, Transport = "tailscale-p2p", MemberCount = 1, LastSeenUtc = DateTimeOffset.UtcNow };
            server.Signature = _identity.Sign(ServerSigningValue(server));
            list.Servers.RemoveAll(item => item.Id == server.Id); list.Servers.Add(server);
        });
    }

    private void RenderLists()
    {
        string search = SearchBox.Text.Trim();
        IEnumerable<SocketChatDirectoryUser> validUsers = _masterList.Users.Where(IsValidUser).Where(u => u.Fingerprint != _identity.Fingerprint);
        if (search.Length > 0) validUsers = validUsers.Where(u => u.Username.Contains(search, StringComparison.OrdinalIgnoreCase) || u.Fingerprint.Contains(search, StringComparison.OrdinalIgnoreCase));
        _people.Clear(); _friends.Clear(); _requests.Clear(); _servers.Clear();
        foreach (var user in validUsers.OrderBy(u => u.Username))
        {
            var item = new PersonDirectoryItem(user, _profile.FriendFingerprints.Contains(user.Fingerprint, StringComparer.Ordinal));
            _people.Add(item); if (item.IsFriend) _friends.Add(item);
        }
        foreach (var request in _masterList.FriendRequests.Where(r => r.ToFingerprint == _identity.Fingerprint && !r.AcceptedUtc.HasValue && IsValidRequest(r)))
        {
            var user = _masterList.Users.FirstOrDefault(u => u.Fingerprint == request.FromFingerprint && IsValidUser(u));
            if (user != null) _requests.Add(new PersonDirectoryItem(user, false, request));
        }
        foreach (var server in _masterList.Servers.Where(IsValidServer).Where(s => search.Length == 0 || s.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name)) _servers.Add(new ServerDirectoryItem(server));
    }

    private void FriendAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PersonDirectoryItem item || item.IsFriend) return;
        Service.Update(list => { if (list.FriendRequests.Any(r => r.FromFingerprint == _identity.Fingerprint && r.ToFingerprint == item.Fingerprint && !r.AcceptedUtc.HasValue)) return; var request = new SocketChatFriendRequest { FromFingerprint = _identity.Fingerprint, ToFingerprint = item.Fingerprint }; request.Signature = _identity.Sign(RequestSigningValue(request)); list.FriendRequests.Add(request); });
        RefreshDirectory();
    }

    private void AcceptRequest_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PersonDirectoryItem item || item.Request == null) return;
        if (!_profile.FriendFingerprints.Contains(item.Fingerprint, StringComparer.Ordinal)) _profile.FriendFingerprints.Add(item.Fingerprint);
        Service.Update(list => { var request = list.FriendRequests.FirstOrDefault(r => r.Id == item.Request.Id); if (request != null) { request.AcceptedUtc = DateTimeOffset.UtcNow; request.AcceptedSignature = _identity.Sign(request.Id + "|" + request.AcceptedUtc.Value.ToUnixTimeMilliseconds()); } });
        _profileStore.Save(_profile); RefreshDirectory();
    }

    private void RemoveFriend_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is PersonDirectoryItem item) { _profile.FriendFingerprints.RemoveAll(value => value == item.Fingerprint); _profileStore.Save(_profile); RenderLists(); } }
    private void JoinServer_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ServerDirectoryItem item) { ServerJoined?.Invoke(item.Server); Close(); } }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDirectory();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RenderLists(); }

    private static bool IsValidUser(SocketChatDirectoryUser user) { try { return SocketChatDeviceIdentity.FingerprintForPublicKey(user.PublicKey) == user.Fingerprint && SocketChatDeviceIdentity.Verify(user.PublicKey, UserSigningValue(user.Fingerprint, user.Username, user.BanIdentityKey), user.Signature); } catch { return false; } }
    private bool IsValidServer(SocketChatDirectoryServer server) { var host = _masterList.Users.FirstOrDefault(u => u.Fingerprint == server.HostFingerprint && IsValidUser(u)); return host != null && SocketChatDeviceIdentity.Verify(host.PublicKey, ServerSigningValue(server), server.Signature); }
    private bool IsValidRequest(SocketChatFriendRequest request) { var sender = _masterList.Users.FirstOrDefault(u => u.Fingerprint == request.FromFingerprint && IsValidUser(u)); return sender != null && SocketChatDeviceIdentity.Verify(sender.PublicKey, RequestSigningValue(request), request.Signature); }
    private bool IsValidAcceptedRequest(SocketChatFriendRequest request) { if (!IsValidRequest(request) || !request.AcceptedUtc.HasValue) return false; var target = _masterList.Users.FirstOrDefault(u => u.Fingerprint == request.ToFingerprint && IsValidUser(u)); return target != null && SocketChatDeviceIdentity.Verify(target.PublicKey, request.Id + "|" + request.AcceptedUtc.Value.ToUnixTimeMilliseconds(), request.AcceptedSignature); }
    private static string UserSigningValue(string fingerprint, string username, string banKey) => fingerprint + "|" + username + "|" + banKey;
    private static string ServerSigningValue(SocketChatDirectoryServer server) => string.Join("|", server.Id, server.Name, server.HostFingerprint, server.Endpoint, server.Transport);
    private static string RequestSigningValue(SocketChatFriendRequest request) => string.Join("|", request.Id, request.FromFingerprint, request.ToFingerprint, request.CreatedUtc.ToUnixTimeMilliseconds());
}

public sealed class PersonDirectoryItem
{
    public string Fingerprint { get; }
    public string FingerprintDisplay => string.Join("-", Enumerable.Range(0, Fingerprint.Length / 4).Select(i => Fingerprint.Substring(i * 4, 4)));
    public string Username { get; }
    public bool IsFriend { get; }
    public string FriendAction => IsFriend ? "Friends" : "Add friend";
    public string Presence { get; }
    public SocketChatFriendRequest? Request { get; }
    public PersonDirectoryItem(SocketChatDirectoryUser user, bool isFriend, SocketChatFriendRequest? request = null) { Fingerprint = user.Fingerprint; Username = user.Username; IsFriend = isFriend; Request = request; Presence = DateTimeOffset.UtcNow - user.LastSeenUtc < TimeSpan.FromMinutes(5) ? "Online" : "Last seen " + user.LastSeenUtc.LocalDateTime.ToString("g"); }
}

public sealed class ServerDirectoryItem
{
    public SocketChatDirectoryServer Server { get; }
    public string Name => Server.Name;
    public string Detail => Server.MemberCount + " member(s) · " + Server.Transport;
    public string Presence => DateTimeOffset.UtcNow - Server.LastSeenUtc < TimeSpan.FromMinutes(5) ? "Online" : "Offline";
    public ServerDirectoryItem(SocketChatDirectoryServer server) { Server = server; }
}
