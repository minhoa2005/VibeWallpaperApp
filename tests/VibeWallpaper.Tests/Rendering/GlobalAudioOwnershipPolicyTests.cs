using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Rendering.Video;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Rendering;

public sealed class GlobalAudioOwnershipPolicyTests
{
    [Fact]
    public async Task SelectOwnerAsync_PersistsThenMutesOldBeforeUnmutingNewAcrossGroups()
    {
        var old = new FakeAudioEndpoint("A", activeVideo: true, 20);
        var next = new FakeAudioEndpoint("B", activeVideo: true, 73);
        old.Muted = false;
        var events = new List<string>();
        old.Events = next.Events = events;
        var state = State(new MonitorIdentity("A"));
        var store = new InMemoryStateStore(state);
        var policy = new GlobalAudioOwnershipPolicy(store);

        var updated = await policy.SelectOwnerAsync(
            state, new MonitorIdentity("B"), [old, next], TestContext.Current.CancellationToken);

        Assert.Equal("B", updated.AudioOwner?.Key);
        Assert.Equal(["A:mute", "B:volume:73", "B:unmute"], events);
        Assert.False(events.Contains("B:unmute") && !old.Muted);
        Assert.Equal("B", store.State.AudioOwner?.Key);
    }

    [Fact]
    public async Task SelectOwnerAsync_SaveFailureRetainsOldAudibleOwner()
    {
        var old = new FakeAudioEndpoint("A", activeVideo: true, 20) { Muted = false };
        var next = new FakeAudioEndpoint("B", activeVideo: true, 73);
        var state = State(new MonitorIdentity("A"));
        var store = new InMemoryStateStore(state) { NextSaveFailure = new IOException("disk full") };
        var policy = new GlobalAudioOwnershipPolicy(store);

        await Assert.ThrowsAsync<IOException>(() => policy.SelectOwnerAsync(
            state, new MonitorIdentity("B"), [old, next], TestContext.Current.CancellationToken));

        Assert.False(old.Muted);
        Assert.True(next.Muted);
        Assert.Equal("A", store.State.AudioOwner?.Key);
    }

    [Fact]
    public async Task SelectOwnerAsync_UnmuteFailureRollsBackAudibleStateAndDoesNotPersistNewOwner()
    {
        var old = new FakeAudioEndpoint("A", activeVideo: true, 20) { Muted = false, CurrentVolumePercent = 20 };
        var next = new FakeAudioEndpoint("B", activeVideo: true, 73)
        {
            CurrentVolumePercent = 7,
            FailNextUnmute = true,
        };
        var state = State(new MonitorIdentity("A"));
        var store = new InMemoryStateStore(state);
        var policy = new GlobalAudioOwnershipPolicy(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SelectOwnerAsync(
            state, new MonitorIdentity("B"), [old, next], TestContext.Current.CancellationToken));

        Assert.False(old.Muted);
        Assert.Equal(20, old.CurrentVolumePercent);
        Assert.True(next.Muted);
        Assert.Equal(7, next.CurrentVolumePercent);
        Assert.Equal("A", store.State.AudioOwner?.Key);
        Assert.Equal(0, store.SaveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_UnavailableSelectedOwnerMeansSilenceWithoutFallback(bool suspended)
    {
        var selected = new FakeAudioEndpoint("A", activeVideo: !suspended, 20) { Muted = false };
        var other = new FakeAudioEndpoint("B", activeVideo: true, 73) { Muted = false };
        if (!suspended) selected.IsConnected = false;
        var state = State(new MonitorIdentity("A"));
        var policy = new GlobalAudioOwnershipPolicy(new InMemoryStateStore(state));

        policy.Apply(state, [selected, other]);

        Assert.True(selected.Muted);
        Assert.True(other.Muted);
    }

    private static PersistedState State(MonitorIdentity owner) =>
        new(1, [], [], [], owner);

    private sealed class FakeAudioEndpoint(string key, bool activeVideo, int volume) : IVideoAudioEndpoint
    {
        public MonitorIdentity Output { get; } = new(key);
        public bool IsConnected { get; set; } = true;
        public bool IsActiveVideo { get; set; } = activeVideo;
        public bool IsSuspended => !IsActiveVideo;
        public int PersistedVolumePercent { get; } = volume;
        public int CurrentVolumePercent { get; set; }
        public bool Muted { get; set; } = true;
        public bool IsMuted => Muted;
        public int VolumePercent => CurrentVolumePercent;
        public bool FailNextUnmute { get; set; }
        public List<string>? Events { get; set; }
        public void SetMuted(bool muted)
        {
            if (!muted && FailNextUnmute)
            {
                FailNextUnmute = false;
                throw new InvalidOperationException("unmute failed");
            }
            Muted = muted;
            Events?.Add($"{Output.Key}:{(muted ? "mute" : "unmute")}");
        }
        public void SetVolume(int volumePercent)
        {
            CurrentVolumePercent = volumePercent;
            Events?.Add($"{Output.Key}:volume:{volumePercent}");
        }
    }
}
