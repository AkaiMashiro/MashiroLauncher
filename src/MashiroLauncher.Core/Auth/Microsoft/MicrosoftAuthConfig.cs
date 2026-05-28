namespace MashiroLauncher.Core.Auth.Microsoft;

public static class MicrosoftAuthConfig
{
    // Mojang's well-known public Minecraft client id, registered with Microsoft's
    // consumer Live OAuth service (login.live.com). Used by the official Minecraft
    // Launcher itself. Microsoft tolerates third-party use; the OAuth consent
    // screen displays "Minecraft" as the requesting app.
    //
    // Note: this id is NOT registered in Azure AD ("Microsoft Accounts" tenant),
    // so the Azure AD endpoints (login.microsoftonline.com) and device-code flow
    // both fail. We use Live OAuth Authorization Code Flow with the OOB redirect
    // that's registered for this id.
    public const string ClientId = "00000000402b5328";

    // Live OAuth endpoints
    public const string AuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";
    public const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    // Out-of-band redirect — Microsoft shows a near-blank "success" page whose
    // URL contains ?code=... The user copies that URL back into our launcher
    // and we extract the code from it.
    public const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";

    public const string Scope = "XboxLive.signin offline_access";

    public const string XblAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    public const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    public const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
}
