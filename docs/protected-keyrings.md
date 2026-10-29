# Protected KNX keyring imports

`ProtectedKnxKeyringImporter` reads password-protected project exports in
`http://knx.org/xml/keyring/1`. It bounds XML size, depth, elements and attributes,
disables DTDs and verifies the complete canonical signature before decrypting any
field. Wrong passwords, changed signatures, unsupported extensions and partial
failures return no imported key material. Error messages contain neither source
XML nor supplied credentials.

The caller supplies an expected project label. This signed label is compared
exactly; it is not a globally unique project identifier or proof of ownership.
Tunnelling selection additionally requires the exact host individual address,
user ID and tunnel individual address. Metadata-only interfaces may be inspected,
but cannot supply incomplete credentials. Password fields remain UTF-8 passwords,
separate from derived IP Secure keys.

Imported Backbone, Interface, GroupAddresses and Device secrets belong to the
returned disposable keyring. Exporting a secret creates a detached copy; the
caller must clear that copy. Disposing the keyring clears its owned buffers and
prevents later exports. The importer does not save a decrypted keyring or modify
the project. Input files, passwords and any separately copied keys belong in
private storage outside the source checkout.

Exported device sequence numbers are historical metadata. They cannot bootstrap
a fresh [Data Secure counter store](data-secure.md) without trusted provisioning
or authenticated synchronization. Certificate/FDSK backups, unsigned or plaintext
XML and an OS credential vault are outside this import profile. Workbench and
MCP credential controls are separate integration work.

The maintained suite checks independent synthetic signed envelopes, published
encrypted field pairs, tampering, limits, exact selection and secret ownership.
It requires no private project or manufacturer export.
All 101 focused checks and the complete combined bus suite pass on Windows and
Ubuntu 26.04. These are import/protocol checks, not Secure-device commissioning.

Public algorithm and format references:

- [KNX keyring file format](https://support.knx.org/hc/en-us/articles/18968368409874-Keyring-File-Format)
- [Using keyrings outside ETS/Falcon](https://support.knx.org/hc/en-us/articles/360001582259-Use-keyring-outside-ETS-Falcon-SDK)
- [XKNX keyring implementation](https://github.com/XKNX/xknx/blob/82e1a851ad9e31669bd8b7dbed59e6066bb7a9f7/xknx/secure/keyring.py)
- [Calimero keyring implementation](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/secure/Keyring.java)
