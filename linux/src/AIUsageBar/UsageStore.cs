using AIUsageBar.Core;
using Avalonia.Threading;

namespace AIUsageBar;

/// <summary>One agent in the Agents tab. <paramref name="HasData"/>: its usage loaded at least once on this computer.</summary>
internal sealed record AgentRow(ProviderId Id, SetupStatus Status, bool IsShown, bool HasData, string? Plan);

internal sealed class ProviderState
{
    public ProviderSnapshot? Snapshot { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public UsageException? Error { get; set; }
    public bool IsLoading { get; set; }
    /// <summary>The last request, successful or not.</summary>
    public DateTimeOffset? LastAttempt { get; set; }
    public RateLimitBackoff Backoff { get; } = new();

    /// <summary>Waiting out a "too many requests": a notice, not a warning.</summary>
    public bool IsRateLimited => Error?.IsRateLimit == true;

    /// <summary>An error worth flagging on the tray icon and the tab.</summary>
    public bool HasWarning => Error is not null && !IsRateLimited;

    public string? MessageFor(ProviderId id, DateTimeOffset? now = null) => Error?.MessageFor(id, Backoff.Until, now);
}

/// <summary>
/// Owns the per-service state and the settings (same behavior as the Windows app's store). Lives on
/// the UI thread; fetches run in the background and their results are applied back here.
/// </summary>
internal sealed class UsageStore
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);
    /// <summary>Opening the popup, and every <see cref="OpenTick"/> while it stays open, asks again the agents asked longer ago than this.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan OpenTick = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<IUsageProvider> providers;
    private readonly Dictionary<ProviderId, ProviderState> states = [];
    private DispatcherTimer? timer;
    /// <summary>Runs while the popup is open.</summary>
    private DispatcherTimer? openTimer;
    /// <summary>Asks a rate-limited agent again as soon as its wait ends.</summary>
    private DispatcherTimer? retryTimer;
    private DateTimeOffset lastTick = DateTimeOffset.Now;

    public UsageStore(AppSettings settings, IReadOnlyList<IUsageProvider> providers)
    {
        Settings = settings;
        this.providers = providers;
        UpdateVisibleProviders();
        LoadCache();
    }

    /// <summary>Fixed data, no network: used by <c>--snapshot</c> and <c>--demo</c>.</summary>
    public UsageStore(AppSettings settings, IEnumerable<ProviderSnapshot> snapshots)
    {
        Settings = settings;
        providers = [];
        foreach (var snapshot in snapshots)
        {
            states[snapshot.Provider] = new ProviderState { Snapshot = snapshot, LastUpdated = DateTimeOffset.Now };
        }
        VisibleProviders = [.. states.Keys.Order()];
    }

    /// <summary>Every agent the app reads, in display order.</summary>
    public static IReadOnlyList<IUsageProvider> AllProviders() =>
        [new ClaudeProvider(), new CodexProvider(), new CopilotProvider(), new CursorProvider(), new GeminiProvider(), new GrokBotProvider(null, keyring: new SecretToolKeyring())];

    public event Action? Changed;

    public AppSettings Settings { get; }

    /// <summary>Services shown, in display order.</summary>
    public IReadOnlyList<ProviderId> VisibleProviders { get; private set; } = [];

    // MARK: Tabs and onboarding

    private string? requestedTab;

    /// <summary>The tab the popup shows: a provider's key or <see cref="PopupTabs.AgentsKey"/>.</summary>
    public string CurrentTab =>
        PopupTabs.Resolve(requestedTab ?? Settings.SelectedProvider, VisibleProviders, Settings.OnboardingDismissed);

    public bool ShowTabStrip => Settings.Layout == PopupLayout.Tabs && PopupTabs.ShowStrip(VisibleProviders.Count);

    /// <summary>"All AIs together": every agent on one screen, unless the Agents tab was asked for.</summary>
    public bool ShowsAllProviders => Settings.Layout == PopupLayout.List && CurrentTab != PopupTabs.AgentsKey;

    public void SelectTab(string key)
    {
        requestedTab = key;
        if (key != PopupTabs.AgentsKey) Settings.SelectedProvider = key;
        Settings.Save();
        Changed?.Invoke();
    }

    public void SelectProvider(ProviderId id) => SelectTab(id.Key());

    /// <summary>Picks a tab without saving anything: used by <c>--snapshot --tab</c>.</summary>
    public void PreviewTab(string key) => requestedTab = key;

    /// <summary>"Set up agents…": opens the Agents tab, also with a single agent.</summary>
    public void ShowAgents() => SelectTab(PopupTabs.AgentsKey);

    public void DismissOnboarding()
    {
        Settings.OnboardingDismissed = true;
        requestedTab = VisibleProviders.Count > 0 ? VisibleProviders[0].Key() : null;
        Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// The Agents tab's "Show" box. Ticking it shows the agent even when its usage does not load
    /// (with the error), unticking hides it even when it works.
    /// </summary>
    public void SetProviderHidden(ProviderId id, bool hidden)
    {
        UpdateSettings(() =>
        {
            Settings.HiddenProviders.RemoveAll(key => key == id.Key());
            Settings.ShownProviders.RemoveAll(key => key == id.Key());
            (hidden ? Settings.HiddenProviders : Settings.ShownProviders).Add(id.Key());
            UpdateVisibleProviders();
        });
        _ = RefreshAsync();
    }

    /// <summary>One row per supported agent, connected or not, for the Agents tab.</summary>
    public IReadOnlyList<AgentRow> AgentRows() =>
    [
        .. ProviderCatalog.All.Select(descriptor =>
        {
            var provider = providers.FirstOrDefault(p => p.Id == descriptor.Id);
            var status = provider?.Detect() ?? (State(descriptor.Id).Snapshot is null ? SetupStatus.NeedsLogin : SetupStatus.Connected);
            var snapshot = State(descriptor.Id).Snapshot;
            return new AgentRow(descriptor.Id, status, VisibleProviders.Contains(descriptor.Id), snapshot is not null, snapshot?.PlanName);
        }),
    ];

    public ProviderState State(ProviderId id) => states.TryGetValue(id, out var state) ? state : states[id] = new ProviderState();

    public bool IsLoading => VisibleProviders.Any(id => State(id).IsLoading);

    /// <summary>The oldest successful update, so a stale service is not hidden.</summary>
    public DateTimeOffset? LastUpdated =>
        VisibleProviders.Select(id => State(id).LastUpdated).Where(d => d is not null).Min();

    // MARK: Settings

    public void SetStyle(TrayStyle style) => UpdateSettings(() => Settings.Style = style);

    public void SetIconColor(TrayIconColor color) => UpdateSettings(() => Settings.IconColor = color);

    public void SetLayout(PopupLayout layout) => UpdateSettings(() => Settings.Layout = layout);

    public void SetDisplay(ProviderId provider, TrayDisplay display) =>
        UpdateSettings(() => Settings.Displays[provider.Key()] = display);

    public void SetTabLimit(ProviderId provider, string key) =>
        UpdateSettings(() => Settings.TabLimits[provider.Key()] = key);

    /// <summary>What the agent's tab can show, for its settings menu.</summary>
    public IReadOnlyList<string> TabLimitOptions(ProviderId provider) =>
        TabLimit.Options(State(provider).Snapshot?.Items ?? [], Settings.TabLimitFor(provider));

    public void SetColorAlerts(bool enabled) => UpdateSettings(() => Settings.ColorAlerts = enabled);

    public void SetLanguage(AppLanguage language) => UpdateSettings(() =>
    {
        Settings.LanguageValue = language;
        Localization.Current = language;
    });

    private void UpdateSettings(Action change)
    {
        change();
        Settings.Save();
        Changed?.Invoke();
    }

    // MARK: Refreshing

    public void Start()
    {
        _ = RefreshAsync();
        // Ticks every 30 s and refreshes every 3 minutes. A tick long after the last one means
        // the computer slept: then everything is refreshed at once (an agent still waiting out
        // a rate limit keeps waiting).
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        var lastRefresh = DateTimeOffset.Now;
        timer.Tick += async (_, _) =>
        {
            var now = DateTimeOffset.Now;
            var resumed = now - lastTick > TimeSpan.FromMinutes(2);
            lastTick = now;
            if (!resumed && now - lastRefresh < RefreshInterval) return;
            lastRefresh = now;
            Resumed?.Invoke();
            await RefreshAsync();
        };
        timer.Start();
    }

    /// <summary>Raised when a refresh is due, also after the computer wakes up (the updater checks then).</summary>
    public event Action? Resumed;

    /// <summary>Refreshes what is stale now and every minute while the popup stays open.</summary>
    public void PopupOpened()
    {
        _ = RefreshIfStaleAsync();
        if (openTimer is null)
        {
            openTimer = new DispatcherTimer { Interval = OpenTick };
            openTimer.Tick += async (_, _) => await RefreshIfStaleAsync();
        }
        openTimer.Start();
    }

    public void PopupClosed() => openTimer?.Stop();

    /// <summary>The agents not asked in the last minute.</summary>
    public Task RefreshIfStaleAsync()
    {
        var now = DateTimeOffset.Now;
        return RefreshAsync(state => state.LastAttempt is not { } asked || now - asked >= StaleAfter);
    }

    /// <summary>
    /// Every agent, except one waiting out a "too many requests": clicking refresh then would only make
    /// the server wait longer, so its last numbers stay until the wait ends. Also fetches agents that are
    /// signed in but not shown yet, so one appears as soon as its usage loads.
    /// </summary>
    public Task RefreshAsync() => RefreshAsync(_ => true);

    private async Task RefreshAsync(Func<ProviderState, bool> wanted)
    {
        UpdateVisibleProviders();
        var now = DateTimeOffset.Now;
        var candidates = Candidates().Select(p => p.Id).ToHashSet();
        var targets = providers
            .Where(p => candidates.Contains(p.Id)
                && State(p.Id) is { IsLoading: false } state && !state.Backoff.IsWaiting(now) && wanted(state))
            .ToList();
        if (targets.Count == 0) return;

        foreach (var provider in targets)
        {
            State(provider.Id).IsLoading = true;
            State(provider.Id).LastAttempt = now;
        }
        Changed?.Invoke();

        var results = await Task.WhenAll(targets.Select(async provider =>
        {
            try
            {
                return (provider.Id, Result: await provider.FetchAsync(), Error: (UsageException?)null);
            }
            catch (UsageException error)
            {
                return (provider.Id, Result: (ProviderFetchResult?)null, Error: error);
            }
            catch (Exception error)
            {
                return (provider.Id, Result: (ProviderFetchResult?)null, Error: new UsageException(UsageErrorKind.Decoding, inner: error));
            }
        }));

        foreach (var (id, result, error) in results)
        {
            var state = State(id);
            state.IsLoading = false;
            if (result is not null)
            {
                state.Snapshot = result.Snapshot;
                state.LastUpdated = DateTimeOffset.Now;
                state.Error = null;
                state.Backoff.Reset();
                SaveCache(id, result.Cache);
            }
            else
            {
                state.Error = error;
                if (error is { IsRateLimit: true }) state.Backoff.Record(error.RetryAfter, DateTimeOffset.Now);
            }
        }
        ScheduleRetry();
        UpdateVisibleProviders();
        Changed?.Invoke();
    }

    /// <summary>Asks the rate-limited agents again when the first wait ends, instead of at the next 3-minute tick.</summary>
    private void ScheduleRetry()
    {
        retryTimer?.Stop();
        var now = DateTimeOffset.Now;
        var waits = states.Values
            .Where(state => state.IsRateLimited && state.Backoff.IsWaiting(now))
            .Select(state => state.Backoff.Until!.Value)
            .ToList();
        if (waits.Count == 0) return;
        if (retryTimer is null)
        {
            var retry = new DispatcherTimer();
            retry.Tick += async (_, _) =>
            {
                retry.Stop();
                await RefreshAsync(state => state.IsRateLimited);
            };
            retryTimer = retry;
        }
        retryTimer.Interval = waits.Min() - now + TimeSpan.FromSeconds(1);
        retryTimer.Start();
    }

    /// <summary>Signed in on this computer and not switched off by the user.</summary>
    private IEnumerable<IUsageProvider> Candidates() =>
        providers.Where(p => !Settings.HiddenProviders.Contains(p.Id.Key()) && p.IsAvailable());

    /// <summary>
    /// A login alone is not enough to show an agent: the gh CLI, for one, can be signed in without a
    /// Copilot plan. An agent shows up once its usage has loaded on this computer (it stays after that,
    /// also when its login later expires), or when the user ticked Show for it.
    /// </summary>
    private void UpdateVisibleProviders()
    {
        if (providers.Count == 0) return;
        VisibleProviders = [.. Candidates()
            .Where(p => State(p.Id).Snapshot is not null || Settings.ShownProviders.Contains(p.Id.Key()))
            .Select(p => p.Id)];
    }

    // MARK: Cache (usage responses only, never tokens)

    private static string CachePath(ProviderId id) => Path.Combine(AppSettings.CacheDirectory, $"{id.Key()}.json");

    private static void SaveCache(ProviderId id, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.CacheDirectory);
            File.WriteAllBytes(CachePath(id), data);
        }
        catch (Exception)
        {
            // The cache only speeds up the next launch.
        }
    }

    private void LoadCache()
    {
        foreach (var provider in providers)
        {
            var path = CachePath(provider.Id);
            try
            {
                if (!File.Exists(path) || provider.SnapshotFromCache(File.ReadAllBytes(path)) is not { } snapshot) continue;
                states[provider.Id] = new ProviderState
                {
                    Snapshot = snapshot,
                    LastUpdated = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                };
            }
            catch (Exception)
            {
                // Ignore an unreadable cache.
            }
        }
    }
}
