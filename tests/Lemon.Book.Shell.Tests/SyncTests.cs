using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lemon.Book.Shell.Contents;
using Lemon.Book.Shell.Infrastructures.Settings;
using Lemon.Pet.Look;
using Lemon.Book.Shell.Sync;
using Xunit;

namespace Lemon.Book.Shell.Tests;

/// <summary>
/// Signing in and syncing: what syncs and what stays on the device, the events read off changes, how two devices'
/// progress is merged, and whole syncs between devices through a fake sync server (with the server's own rules).
/// </summary>
public sealed class SyncTests
{
    private static readonly Uri Server = new("https://sync.example.com/api/");
    private static readonly DateTime T0 = new(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- the store

    [Fact]
    public void Changes_to_synced_settings_wait_to_be_sent_and_device_settings_never_do()
    {
        var store = new SyncedSettingsStore(new MemorySettingsStore(), () => T0);

        store.Set(SettingKeys.Games, "a");
        store.Set("window", "{}");
        store.Set("update.feed", "off");

        var pending = Assert.Single(store.PendingChanges());
        Assert.Equal(SettingKeys.Games, pending.Key);
        Assert.Equal("a", pending.Value);
        Assert.Equal(0, pending.BaseVersion);
        Assert.Equal(T0, pending.ChangedAt);
    }

    [Fact]
    public void Writing_the_same_value_again_is_no_change()
    {
        var store = new SyncedSettingsStore(new MemorySettingsStore(), () => T0);
        store.Set(SettingKeys.Games, "a");
        store.Accepted(SettingKeys.Games, "a", 3);

        store.Set(SettingKeys.Games, "a");

        Assert.Empty(store.PendingChanges());
    }

    [Fact]
    public void A_value_changed_while_it_was_being_sent_is_sent_again()
    {
        var store = new SyncedSettingsStore(new MemorySettingsStore(), () => T0);
        store.Set(SettingKeys.Games, "a");
        var sent = store.PendingChanges().Single().Value;
        store.Set(SettingKeys.Games, "a\nb");

        store.Accepted(SettingKeys.Games, sent, 1);

        var pending = Assert.Single(store.PendingChanges());
        Assert.Equal("a\nb", pending.Value);
        Assert.Equal(1, pending.BaseVersion);
    }

    [Fact]
    public void The_journal_survives_a_restart()
    {
        var device = new MemorySettingsStore();
        new SyncedSettingsStore(device, () => T0).Set(SettingKeys.ShowPinyin, bool.TrueString);

        var again = new SyncedSettingsStore(device, () => T0);

        Assert.Equal(SettingKeys.ShowPinyin, Assert.Single(again.PendingChanges()).Key);
    }

    // ---------------------------------------------------------------- events

    [Fact]
    public void Values_from_the_server_are_not_events()
    {
        var store = new SyncedSettingsStore(new MemorySettingsStore(), () => T0);

        store.ApplyFromServer(TestSyncRules.Finished, "a,b", 4);

        Assert.Empty(store.Events.Pending);
        Assert.Empty(store.PendingChanges());
    }

    // ---------------------------------------------------------------- merging

    [Fact]
    public void A_preference_is_the_later_of_the_two()
    {
        Assert.Equal("Dark", SyncMerge.Merge("theme", ("Dark", T0.AddMinutes(1)), ("Light", T0)));
        Assert.Equal("Light", SyncMerge.Merge("theme", ("Dark", T0), ("Light", T0.AddMinutes(1))));
    }

    [Fact]
    public void Clearing_against_a_change_is_the_later_of_the_two()
    {
        Assert.Null(SyncMerge.Merge(ReadingProgress.SettingKey, (null, T0.AddMinutes(1)), ("{}", T0)));
    }

    // ---------------------------------------------------------------- the book's own rules

    [Fact]
    public void The_books_progress_merges_by_its_rules_and_anything_else_is_the_later()
    {
        Assert.Equal("a,b,c", SyncMerge.Merge(TestSyncRules.Finished, ("a,c", T0), ("b,a", T0)));
        Assert.Equal("x\ny", SyncMerge.Merge(SettingKeys.Games, ("y", T0), ("x", T0)));
        Assert.Equal("later", SyncMerge.Merge("unknown", ("later", T0.AddMinutes(1)), ("earlier", T0)));
    }

    [Fact]
    public void The_books_progress_becomes_events_by_its_rules_and_walkthroughs_by_the_shells()
    {
        var store = new SyncedSettingsStore(new MemorySettingsStore(), () => T0);

        store.Set(TestSyncRules.Finished, "a");
        store.Set(SettingKeys.Games, "g1");
        new ReadingProgress(store).MarkWalkthrough("book", "view");

        var types = store.Events.Pending.Select(e => (e.Type, e.Subject)).ToList();
        Assert.Contains(("book-finished", "a"), types);
        Assert.Contains(("game-done", "g1"), types);
        Assert.Contains(("walkthrough-done", "book"), types);
        Assert.All(store.Events.Pending, e => Assert.Equal(T0, e.OccurredAt));
    }

    // ---------------------------------------------------------------- whole syncs

    [Fact]
    public async Task Signing_in_brings_this_devices_progress_into_the_account_and_the_accounts_into_this_device()
    {
        var server = new FakeSyncServer();
        var (tablet, tabletStore) = Device(server);
        tabletStore.Set(TestSyncRules.Finished, "a");
        tabletStore.Set(SettingKeys.Games, "g1");
        await tablet.SignInAsync();

        var (laptop, laptopStore) = Device(server);
        laptopStore.Set(TestSyncRules.Finished, "b");
        var remoteChanged = 0;
        laptop.RemoteChanged += (_, _) => remoteChanged++;
        await laptop.SignInAsync();

        Assert.Equal(SyncState.Synced, laptop.State);
        Assert.Equal("小明", laptop.Session!.UserName);
        Assert.Equal("a,b", laptopStore.Get(TestSyncRules.Finished));
        Assert.Equal("g1", laptopStore.Get(SettingKeys.Games));
        Assert.Equal(1, remoteChanged);
        Assert.Empty(laptopStore.PendingChanges());

        await tablet.SyncNowAsync();
        Assert.Equal("a,b", tabletStore.Get(TestSyncRules.Finished));
        Assert.Equal("a,b", server.Value(TestSyncRules.Finished));
    }

    [Fact]
    public async Task Changes_made_on_two_devices_at_once_are_merged()
    {
        var server = new FakeSyncServer();
        var (a, aStore) = Device(server);
        var (b, bStore) = Device(server);
        await a.SignInAsync();
        await b.SignInAsync();

        aStore.Set(SettingKeys.Games, "x");
        bStore.Set(SettingKeys.Games, "y");
        await a.SyncNowAsync();
        await b.SyncNowAsync();
        await a.SyncNowAsync();

        Assert.Equal("x\ny", server.Value(SettingKeys.Games));
        Assert.Equal("x\ny", aStore.Get(SettingKeys.Games));
        Assert.Equal("x\ny", bStore.Get(SettingKeys.Games));
    }

    [Fact]
    public async Task Events_are_sent_once()
    {
        var server = new FakeSyncServer();
        var (device, store) = Device(server);
        await device.SignInAsync();

        store.Set(SettingKeys.Games, "g1");
        await device.SyncNowAsync();
        await device.SyncNowAsync();

        Assert.Equal(["game-done"], server.Events.Select(e => e.Type));
        Assert.Empty(store.Events.Pending);
    }

    [Fact]
    public async Task Offline_the_progress_stays_on_the_device_until_the_server_is_back()
    {
        var server = new FakeSyncServer();
        var (device, store) = Device(server);
        await device.SignInAsync();

        server.Down = true;
        store.Set(SettingKeys.Games, "g1");
        await device.SyncNowAsync();
        Assert.Equal(SyncState.Offline, device.State);
        Assert.Single(store.PendingChanges());

        server.Down = false;
        await device.SyncNowAsync();
        Assert.Equal(SyncState.Synced, device.State);
        Assert.Equal("g1", server.Value(SettingKeys.Games));
    }

    [Fact]
    public async Task A_refused_refresh_asks_to_sign_in_again_and_keeps_the_progress()
    {
        var now = T0;
        var server = new FakeSyncServer();
        var (device, store) = Device(server, () => now);
        await device.SignInAsync();

        server.RefuseRefresh = true;
        now = now.AddHours(2);
        store.Set(SettingKeys.Games, "g1");
        await device.SyncNowAsync();

        Assert.Equal(SyncState.NeedsSignIn, device.State);
        Assert.Equal("g1", store.Get(SettingKeys.Games));
        Assert.True(device.IsSignedIn);
    }

    [Fact]
    public async Task Signing_out_keeps_the_progress_on_the_device()
    {
        var server = new FakeSyncServer();
        var (device, store) = Device(server);
        store.Set(SettingKeys.Games, "g1");
        await device.SignInAsync();

        device.SignOut();

        Assert.Equal(SyncState.SignedOut, device.State);
        Assert.Equal("g1", store.Get(SettingKeys.Games));
        Assert.Null(store.Inner.Get(SyncService.SessionKey));
    }

    // ---------------------------------------------------------------- a parent's children

    [Fact]
    public async Task Choosing_a_child_keeps_their_progress_apart_from_the_accounts()
    {
        var server = new FakeSyncServer();
        var (laptop, laptopStore) = Device(server);
        laptopStore.Set(TestSyncRules.Finished, "a");
        await laptop.SignInAsync();

        var child = await laptop.AddChildAsync(" 小柠檬 ", TestContext.Current.CancellationToken);
        await laptop.ChooseChildAsync(child);
        laptopStore.Set(TestSyncRules.Finished, "a,b");
        await laptop.SyncNowAsync();

        Assert.Equal("小柠檬", laptop.Child!.Name);
        Assert.Equal("a,b", server.Value(TestSyncRules.Finished, child.Id));
        Assert.Equal("a", server.Value(TestSyncRules.Finished));

        // Another device of the family picks the same child and reads on where they were.
        var (tablet, tabletStore) = Device(server);
        await tablet.SignInAsync();
        await tablet.ChooseChildAsync(Assert.Single(await tablet.GetChildrenAsync(TestContext.Current.CancellationToken)));

        Assert.Equal("a,b", tabletStore.Get(TestSyncRules.Finished));
    }

    [Fact]
    public async Task The_child_is_remembered_on_the_device_and_forgotten_on_signing_out()
    {
        var server = new FakeSyncServer();
        var memory = new MemorySettingsStore();
        var (device, _) = Device(server, inner: memory);
        await device.SignInAsync();
        await device.ChooseChildAsync(await device.AddChildAsync("小柠檬", TestContext.Current.CancellationToken));

        var (restarted, _) = Device(server, inner: memory);
        Assert.Equal("小柠檬", restarted.Child?.Name);

        restarted.SignOut();
        Assert.Null(restarted.Child);
        Assert.Null(memory.Get(SyncService.ChildKey));
    }

    [Fact]
    public async Task A_child_removed_from_the_account_is_no_longer_synced()
    {
        var server = new FakeSyncServer();
        var (device, store) = Device(server);
        await device.SignInAsync();
        var child = await device.AddChildAsync("小柠檬", TestContext.Current.CancellationToken);
        await device.ChooseChildAsync(child);

        server.RemoveChild(child.Id);
        store.Set(SettingKeys.Games, "g1");
        await device.SyncNowAsync();

        Assert.Null(device.Child);
        Assert.Equal(SyncState.Offline, device.State);
        await device.SyncNowAsync();
        Assert.Equal("g1", server.Value(SettingKeys.Games));
    }

    [Fact]
    public void Without_a_sync_server_sign_in_is_not_offered()
    {
        var store = new SyncedSettingsStore(new MemorySettingsStore());

        var sync = new SyncService(store, server: null, new FakeFlow());

        Assert.False(sync.IsAvailable);
        Assert.Equal(SyncState.Unavailable, sync.State);
    }

    [Theory]
    [InlineData("https://host.example.com/hub-api", "https://host.example.com/hub-api/")]
    [InlineData("http://localhost:44400/", "http://localhost:44400/")]
    [InlineData("http://host.example.com/", null)]
    [InlineData("not an address", null)]
    [InlineData("", null)]
    public void The_sync_server_must_be_https_except_on_this_computer(string text, string? expected)
    {
        Assert.Equal(expected, SyncServer.Parse(text)?.ToString());
    }

    // ---------------------------------------------------------------- the pet over the book (Lemon.Pet.Core)

    private static readonly Point FigureCentre = new(160, 360);

    /// <summary>The pet on this device's sign-in, reaching the same fake server.</summary>
    private static (SyncService Sync, SyncedSettingsStore Store) Device(
        FakeSyncServer server, Func<DateTime>? now = null, MemorySettingsStore? inner = null)
    {
        now ??= () => T0;
        var store = new SyncedSettingsStore(inner ?? new MemorySettingsStore(), now);
        var sync = new SyncService(store, Server, new FakeFlow(), new HttpClient(server), now);
        return (sync, store);
    }

    /// <summary>Answers the sign-in page at once, as the reader would after typing their password.</summary>
    private sealed class FakeFlow : ISignInFlow
    {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:47613/signin");

        public Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken)
        {
            var state = OidcClient.ParseQuery(authorizeUrl.Query)["state"];
            return Task.FromResult<string?>($"?code=the-code&state={state}");
        }
    }

    /// <summary>
    /// The sign-in service, the learning API and the family's children, in memory, with the same sync rules. Each
    /// child's records and counter are their own (<see cref="Guid.Empty"/>: the account's own).
    /// </summary>
    private sealed class FakeSyncServer : HttpMessageHandler
    {
        private readonly Dictionary<Guid, Dictionary<string, (string? Value, long Version, DateTime ChangedAt)>> _byChild = [];
        private readonly Dictionary<Guid, long> _counters = [];
        private readonly List<(Guid Id, string Name)> _children = [];

        public bool Down { get; set; }

        public bool RefuseRefresh { get; set; }

        public List<(Guid Id, string Type)> Events { get; } = [];

        public string? Value(string key, Guid child = default) => RecordsOf(child).GetValueOrDefault(key).Value;

        public void RemoveChild(Guid id) => _children.RemoveAll(c => c.Id == id);

        /// <summary>Pets by child (<see cref="Guid.Empty"/>: the account's own), as the pet app would have chosen them.</summary>
        public Dictionary<Guid, JsonObject> Pets { get; } = [];

        public static JsonObject Sunflower(string name, int stage = 0, int points = 0) => new()
        {
            ["id"] = Guid.Parse("11111111-2222-3333-4444-555555555555"), ["species"] = 2, ["name"] = name, ["points"] = points,
            ["progress"] = new JsonObject
            {
                ["stage"] = stage, ["stageCount"] = 7, ["stageCode"] = PetText.SunflowerStages[stage],
                ["pointsIntoStage"] = 0, ["pointsForStage"] = 15, ["round"] = 0, ["skills"] = new JsonArray(), ["isComplete"] = false,
            },
            ["streak"] = 1, ["todayPoints"] = points, ["dailyCap"] = 100, ["missesChild"] = false,
        };

        public static JsonObject Wukong(string name, int stage = 0, int round = 0) => new()
        {
            ["id"] = Guid.Parse("66666666-7777-8888-9999-000000000000"), ["species"] = 1, ["name"] = name, ["points"] = 0,
            ["progress"] = new JsonObject
            {
                ["stage"] = stage, ["stageCount"] = 81, ["stageCode"] = PetText.WukongChapters[round],
                ["pointsIntoStage"] = 0, ["pointsForStage"] = 15, ["round"] = round, ["skills"] = new JsonArray(), ["isComplete"] = false,
            },
            ["streak"] = 1, ["todayPoints"] = 0, ["dailyCap"] = 100, ["missesChild"] = false,
        };

        private Dictionary<string, (string? Value, long Version, DateTime ChangedAt)> RecordsOf(Guid child) =>
            _byChild.TryGetValue(child, out var records) ? records : _byChild[child] = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("No route to host");
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/client", StringComparison.Ordinal))
            {
                return Json("""{"authority":"https://sso.example.com","clientId":"lemon_picture_book","scope":"openid offline_access api"}""");
            }

            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Json("""{"authorization_endpoint":"https://sso.example.com/connect/authorize","token_endpoint":"https://sso.example.com/connect/token"}""");
            }

            if (path.EndsWith("/connect/token", StringComparison.Ordinal))
            {
                if (body.Contains("grant_type=refresh_token", StringComparison.Ordinal) && RefuseRefresh)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
                }

                var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"user-1","name":"小明"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Json($$"""{"access_token":"at","refresh_token":"rt","expires_in":3600,"id_token":"h.{{claims}}.s"}""");
            }

            Assert.Equal("Bearer at", request.Headers.Authorization?.ToString());
            if (path.EndsWith("/family/children", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    var name = JsonNode.Parse(body)!["name"]!.GetValue<string>().Trim();
                    _children.Add((Guid.NewGuid(), name));
                    return Json(Child(_children[^1]).ToJsonString());
                }

                return Json(new JsonArray(_children.Select(c => (JsonNode)Child(c)).ToArray()).ToJsonString());
            }

            // Talking to the pet is not allowed on this account.
            if (path.EndsWith("/app/pet/chat", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (path.EndsWith("/app/pet", StringComparison.Ordinal))
            {
                var petQuery = OidcClient.ParseQuery(request.RequestUri.Query);
                var petChild = petQuery.TryGetValue("child", out var petId) ? Guid.Parse(petId) : Guid.Empty;
                return Pets.TryGetValue(petChild, out var pet) ? Json(pet.ToJsonString()) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var query = OidcClient.ParseQuery(request.RequestUri.Query);
            var child = query.TryGetValue("child", out var id) ? Guid.Parse(id) : Guid.Empty;
            if (child != Guid.Empty && _children.All(c => c.Id != child))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Json(Sync(JsonNode.Parse(body)!, child));
        }

        private static JsonObject Child((Guid Id, string Name) child) => new() { ["id"] = child.Id, ["name"] = child.Name };

        private string Sync(JsonNode input, Guid child)
        {
            var records = RecordsOf(child);
            var counter = _counters.GetValueOrDefault(child);
            var since = input["since"]!.GetValue<long>();
            var accepted = new JsonArray();
            var conflicts = new JsonArray();
            var written = new HashSet<string>();
            foreach (var change in input["changes"]!.AsArray())
            {
                var key = change!["key"]!.GetValue<string>();
                var value = change["value"]?.GetValue<string>();
                var baseVersion = change["baseVersion"]!.GetValue<long>();
                var changedAt = change["changedAt"]!.GetValue<DateTime>();
                written.Add(key);
                var has = records.TryGetValue(key, out var record);
                if (has && record.Value == value)
                {
                    accepted.Add(new JsonObject { ["key"] = key, ["version"] = record.Version });
                }
                else if ((has ? record.Version : 0) != baseVersion && has)
                {
                    conflicts.Add(Record(key, record));
                }
                else
                {
                    records[key] = (value, ++counter, changedAt);
                    accepted.Add(new JsonObject { ["key"] = key, ["version"] = counter });
                }
            }

            foreach (var e in input["events"]!.AsArray())
            {
                var id = e!["id"]!.GetValue<Guid>();
                if (Events.All(x => x.Id != id)) Events.Add((id, e["type"]!.GetValue<string>()));
            }

            var changes = new JsonArray(records.Where(r => r.Value.Version > since && !written.Contains(r.Key))
                .OrderBy(r => r.Value.Version).Select(r => (JsonNode)Record(r.Key, r.Value)).ToArray());
            _counters[child] = counter;
            return new JsonObject { ["cursor"] = counter, ["accepted"] = accepted, ["conflicts"] = conflicts, ["changes"] = changes }
                .ToJsonString();
        }

        private static JsonObject Record(string key, (string? Value, long Version, DateTime ChangedAt) record) => new()
        {
            ["key"] = key, ["value"] = record.Value, ["deleted"] = record.Value is null, ["version"] = record.Version,
            ["changedAt"] = record.ChangedAt,
        };

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
