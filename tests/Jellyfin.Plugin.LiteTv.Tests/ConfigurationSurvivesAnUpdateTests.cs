using System;
using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.LiteTv.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A plugin update must not cost the tokens the running server is already using.
/// <para>
/// Installing a new version replaces the assembly and restarts Jellyfin; the configuration
/// document on disk is the only thing that crosses that boundary, and it is read back by the
/// <b>new</b> <see cref="PluginConfiguration"/>. So the update-safety of a token is not a
/// question about the update path at all - it is a question about whether the document a
/// previous version wrote still deserializes into the same values here.
/// </para>
/// <para>
/// That is silent when it breaks. Renaming a property, changing its type, or making it
/// non-serializable does not fail the read: the element is simply not matched and the property
/// keeps its default, which for both of these is "no token". The visible result is a television
/// whose stream stops at the next tune-in
/// (<see cref="PlaybackTokenIsReusedTests"/>) and trailers back at 360p
/// (<see cref="ProofOfOriginTests"/>) - two symptoms that look like anything but a rename.
/// </para>
/// <para>
/// The fixtures below are therefore written as literal XML rather than by serializing the
/// current class. Round-tripping today's class through itself would pass no matter what it was
/// renamed to, which is precisely the failure being guarded against.
/// </para>
/// </summary>
public class ConfigurationSurvivesAnUpdateTests
{
    private const string PlaybackToken = "0e3a9c1b7d5f4826a0c9e7b3d1f58642";
    private const string StreamToken = "MnQxOTA0Njk0OA==.pot-from-a-television";
    private const string VisitorData = "CgtVLXhZbUZ2Q2s5RSjq7q7ABg%3D%3D";

    private static PluginConfiguration Read(string xml)
    {
        using var reader = new StringReader(xml);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration))
            .Deserialize(reader)!;
    }

    /// <summary>
    /// The document as an older version left it, holding both tokens. Every element here was
    /// written by a version that shipped; none of them may stop being read.
    /// </summary>
    private static string WrittenByAnEarlierVersion(string extra = "") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <ChannelUserName>litetv</ChannelUserName>
          <ChannelUserPassword>not-the-real-one</ChannelUserPassword>
          <ChannelUserToken>{PlaybackToken}</ChannelUserToken>
          <ProofOfOriginToken>{StreamToken}</ProofOfOriginToken>
          <ProofOfOriginVisitorData>{VisitorData}</ProofOfOriginVisitorData>
          <ProofOfOriginMintedUtc>2026-09-03T09:15:00Z</ProofOfOriginMintedUtc>
        {extra}</PluginConfiguration>
        """;

    [Fact]
    public void The_playback_token_a_previous_version_stored_is_still_read()
    {
        Assert.Equal(PlaybackToken, Read(WrittenByAnEarlierVersion()).ChannelUserToken);
    }

    /// <summary>
    /// All three parts, because a token without the visitor id it was minted against is refused
    /// in a way that looks exactly like sending none - so losing any one of them loses the lot.
    /// </summary>
    [Fact]
    public void Every_part_of_the_proof_of_origin_token_is_still_read()
    {
        var config = Read(WrittenByAnEarlierVersion());

        Assert.Equal(StreamToken, config.ProofOfOriginToken);
        Assert.Equal(VisitorData, config.ProofOfOriginVisitorData);
        Assert.Equal(
            new DateTime(2026, 9, 3, 9, 15, 0, DateTimeKind.Utc),
            config.ProofOfOriginMintedUtc!.Value.ToUniversalTime());
    }

    /// <summary>
    /// A downgrade, or a document written before a setting existed. The missing elements take
    /// their defaults, and the tokens - which are present - are untouched by that.
    /// </summary>
    [Fact]
    public void A_document_missing_newer_settings_still_yields_its_tokens()
    {
        var config = Read($"""
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <ChannelUserToken>{PlaybackToken}</ChannelUserToken>
              <ProofOfOriginToken>{StreamToken}</ProofOfOriginToken>
              <ProofOfOriginVisitorData>{VisitorData}</ProofOfOriginVisitorData>
            </PluginConfiguration>
            """);

        Assert.Equal(PlaybackToken, config.ChannelUserToken);
        Assert.Equal(StreamToken, config.ProofOfOriginToken);
        Assert.Null(config.ProofOfOriginMintedUtc);
    }

    /// <summary>
    /// An element this version has dropped must not take the read down with it. XmlSerializer
    /// ignores unknown elements, and this pins that: a version that removes a setting has to be
    /// installable over one that wrote it, or the update loses every value in the document
    /// rather than the one that went away.
    /// </summary>
    [Fact]
    public void An_element_this_version_no_longer_knows_is_ignored_rather_than_fatal()
    {
        var config = Read(WrittenByAnEarlierVersion(
            "  <SomethingThisVersionRemoved>true</SomethingThisVersionRemoved>\n"));

        Assert.Equal(PlaybackToken, config.ChannelUserToken);
        Assert.Equal(StreamToken, config.ProofOfOriginToken);
    }

    /// <summary>
    /// The write half. A configuration holding tokens must serialize them as elements a later
    /// version can find - if a property stopped being serialized, the next save would quietly
    /// write a document with no tokens in it and the loss would happen on the following restart.
    /// </summary>
    [Fact]
    public void Tokens_are_written_back_into_the_document()
    {
        var config = new PluginConfiguration
        {
            ChannelUserToken = PlaybackToken,
            ProofOfOriginToken = StreamToken,
            ProofOfOriginVisitorData = VisitorData,
            ProofOfOriginMintedUtc = new DateTime(2026, 9, 3, 9, 15, 0, DateTimeKind.Utc)
        };

        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
        var document = writer.ToString();

        Assert.Contains("<ChannelUserToken>" + PlaybackToken + "</ChannelUserToken>", document, StringComparison.Ordinal);
        Assert.Contains("<ProofOfOriginToken>" + StreamToken + "</ProofOfOriginToken>", document, StringComparison.Ordinal);
        Assert.Contains("<ProofOfOriginVisitorData>" + VisitorData + "</ProofOfOriginVisitorData>", document, StringComparison.Ordinal);

        // And it survives its own document, which is the restart this all exists for.
        Assert.Equal(PlaybackToken, Read(document).ChannelUserToken);
        Assert.Equal(StreamToken, Read(document).ProofOfOriginToken);
    }
}
