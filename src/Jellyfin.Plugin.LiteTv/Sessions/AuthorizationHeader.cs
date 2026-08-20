namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Reads the fields Jellyfin clients identify themselves with out of their authorization
/// header. The device is the one thing that tells a channel's own playback apart from the
/// same title watched deliberately elsewhere, so it is read the same way everywhere it is
/// needed rather than parsed twice, slightly differently.
/// </summary>
internal static class AuthorizationHeader
{
    /// <summary>Jellyfin accepts its authorization payload under either name.</summary>
    public static readonly string[] Names = ["Authorization", "X-Emby-Authorization"];

    /// <summary>
    /// Reads the device id out of an authorization header value.
    /// </summary>
    /// <param name="authorization">The header value, e.g.
    /// <c>MediaBrowser Client="...", Device="...", DeviceId="...", Token="..."</c>.</param>
    /// <returns>The device id, or null when the header does not carry one.</returns>
    public static string? DeviceId(string authorization)
    {
        const string Field = "DeviceId=";
        var start = authorization.IndexOf(Field, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var value = authorization.AsSpan(start + Field.Length).TrimStart();
        var quoted = value.Length > 0 && value[0] == '"';
        if (quoted)
        {
            value = value[1..];
        }

        var end = value.IndexOf(quoted ? '"' : ',');
        var deviceId = (end < 0 ? value : value[..end]).Trim().ToString();
        return string.IsNullOrEmpty(deviceId) ? null : deviceId;
    }
}
