namespace MashiroLauncher.Core.Auth.Microsoft;

public static class MicrosoftAuthConfig
{
    // Mashiro Launcher's own Azure AD application id, approved by Mojang for the
    // Minecraft authentication APIs. Because it's a custom registration (not the
    // legacy public Minecraft id), we use the Microsoft identity platform v2.0
    // endpoints — NOT the old login.live.com Live OAuth service.
    public const string ClientId = "8a0d48af-5f14-4895-a247-dc2eb93259a0";

    // Microsoft identity platform v2.0 endpoints, "consumers" tenant — Minecraft
    // accounts are personal Microsoft accounts, so we target /consumers/ rather
    // than /common/ to keep the flow MSA-only.
    public const string AuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    public const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    // Redirect URI registered under "Mobile and desktop applications" in the
    // Azure app's Authentication blade. The embedded WebView intercepts the
    // navigation to this URL (its query carries ?code=...). It renders a blank
    // page, so the user never actually sees it.
    public const string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    public const string Scope = "XboxLive.signin offline_access";

    public const string XblAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    public const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    public const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
}
