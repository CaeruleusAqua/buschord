using System.Text.Json.Nodes;
using F = GroupConnectionFixture;

internal static class GroupHostConfigurationFixture
{
    internal static string Write(F fixture)
    {
        var path = Path.Combine(fixture.Root, "registry.json");
        var item = new JsonObject
        {
            ["id"] = "test", ["gateway"] = "127.0.0.1", ["port"] = 3671, ["own_address"] = F.Local.ToString(), ["host_address"] = "1.2.0", ["user_id"] = 4,
            ["keyring_path"] = fixture.Keyring, ["keyring_project"] = "BusChord synthetic keyring", ["keyring_password_env"] = "SYNTHETIC_REGISTRY_PASSWORD_ENV",
            ["expected_keyring_sha256"] = fixture.Reference().ExpectedSourceFingerprint, ["installation_scope"] = "Synthetic physical group installation", ["ordinary_key_epoch"] = "ordinary-1",
            ["counter_state_path"] = fixture.State, ["counter_integrity_env"] = "SYNTHETIC_REGISTRY_INTEGRITY_ENV", ["state_trust"] = "provisioner_confirmed_current_unanchored",
            ["individual_key_sha256"] = new JsonArray(), ["groups"] = new JsonArray(new JsonObject { ["address"] = F.Group.ToString(), ["protection"] = "auth_conf", ["senders"] = new JsonArray(F.Sender.ToString()) }),
            ["plain_groups"] = new JsonArray(), ["plain_peers"] = new JsonArray()
        };
        File.WriteAllText(path, new JsonObject { ["format"] = "BusChord.GroupSecurityHost.v1", ["profiles"] = new JsonArray(item) }.ToJsonString()); return path;
    }
}
