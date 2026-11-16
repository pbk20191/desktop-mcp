## Downloads

| File | For |
|---|---|
| `desktop-mcp-windows.mcpb` | Windows 11 22H2 or later, x64 and ARM64 (the native ARM64 build is chosen automatically) |
| `desktop-mcp-macos.mcpb` | macOS 13 or later, Apple silicon and Intel (one universal binary) |
| `desktop-mcp-codesign.pem` | the public certificate everything here is signed with |
| `signing-*.txt` | SHA-256 and signature details of every file, as the release build checked them |

Install: open the `.mcpb` file with Claude Desktop, or drop it on Settings → Extensions. On macOS, grant the bundled
`desktop-mcp` Accessibility and Screen Recording in System Settings → Privacy & Security.

## Signatures

The executables and both bundles are signed with a self-signed certificate:

- Subject `CN=desktop-mcp (pbk20191)`, valid until 2036-11-16
- SHA-1 thumbprint `88FE207CD26CF6E483BEE389B2FE701D156DD675`
- SHA-256 fingerprint `CA:47:27:03:B6:3D:92:2E:21:81:A0:7D:6A:6E:DB:43:E4:03:A0:77:61:03:54:3F:65:6D:32:91:99:6E:62:30`

Self-signed means the signature proves the files are unchanged and come from the same publisher every release (on
macOS this keeps the Accessibility and Screen Recording grants across updates), but no authority vouches for it:

- **Windows** shows the signature as from an untrusted root, and SmartScreen may warn. To trust it on your PC,
  import `desktop-mcp-codesign.pem` into *Trusted Publishers* and *Trusted Root Certification Authorities* for the
  current user (`certmgr.msc`), after checking the fingerprint above.
- **macOS** is not notarized; Gatekeeper may block a downloaded binary until you allow it in Privacy & Security.
- **`mcpb verify` reports "Extension is not signed"** for these bundles. That is a limitation of the mcpb CLI
  2.1.2: its verification calls a PKCS#7 routine its library does not implement, so it reports every signed bundle
  as unsigned. To check a bundle's signature yourself, run
  [`scripts/verify-mcpb-signature.py`](https://github.com/pbk20191/desktop-mcp/blob/master/scripts/verify-mcpb-signature.py)
  with Python 3 and OpenSSL: `python3 verify-mcpb-signature.py desktop-mcp-windows.mcpb desktop-mcp-codesign.pem`.
