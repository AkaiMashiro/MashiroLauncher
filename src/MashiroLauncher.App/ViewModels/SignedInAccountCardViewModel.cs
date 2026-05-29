using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MashiroLauncher.App.Services;
using MashiroLauncher.Core.Auth;

namespace MashiroLauncher.App.ViewModels;

/// <summary>
/// Wraps a single signed-in <see cref="MicrosoftAccount"/> for display in the
/// AccountSelection screen and Settings → 계정 list. Owns the avatar Bitmap so
/// each row's face loads lazily without blocking the rest of the list.
///
/// The card knows about its parent <see cref="MainWindowViewModel"/> so the
/// ⎋ logout button and the click-to-select action can delegate to existing
/// commands without re-implementing the active-account / sign-out logic.
/// </summary>
public partial class SignedInAccountCardViewModel : ObservableObject
{
    private readonly MainWindowViewModel _parent;

    public SignedInAccountCardViewModel(MicrosoftAccount account, MainWindowViewModel parent, AvatarService avatars)
    {
        Account = account;
        _parent = parent;
        // Avatar paints blank for a moment; the constructor kicks off the
        // Minotar fetch and the bitmap arrives via property change.
        _ = LoadAvatarAsync(avatars);
    }

    public MicrosoftAccount Account { get; }
    public string Username => Account.Username;
    public Guid Uuid => Account.Uuid;

    [ObservableProperty] private Bitmap? _avatar;

    /// <summary>True when this account is the launcher's current active selection.</summary>
    public bool IsActive => _parent.ActiveAccount?.Uuid == Account.Uuid;

    /// <summary>
    /// Called by the parent VM when the active-account pointer changes so the
    /// "현재 활성" badge re-renders without forcing a list rebuild.
    /// </summary>
    public void NotifyActiveChanged() => OnPropertyChanged(nameof(IsActive));

    private async Task LoadAvatarAsync(AvatarService avatars)
    {
        Bitmap? bitmap;
        try { bitmap = await avatars.GetMinecraftHeadAsync(Account.Uuid).ConfigureAwait(false); }
        catch { bitmap = null; }
        await Dispatcher.UIThread.InvokeAsync(() => Avatar = bitmap);
    }

    /// <summary>Switch the launcher to this account and navigate to Play.</summary>
    [RelayCommand]
    private void Select() => _parent.OnAccountSelectedFromList(Account);

    /// <summary>Remove this account from the store (small ⎋ button on the card).</summary>
    [RelayCommand]
    private async Task SignOutAsync() => await _parent.SignOutAccountFromCardAsync(Account);
}
