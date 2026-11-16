#!/usr/bin/env python3
"""Verifies a signed .mcpb against a certificate with OpenSSL: python3 verify-mcpb-signature.py <bundle> <cert.pem>.

`mcpb sign` appends 'MCPB_SIG_V1', the PKCS#7 signature's length (u32, little-endian), the signature (DER, detached,
over the bytes before the block) and 'MCPB_SIG_END'. `mcpb verify` (2.1.2) cannot check it: node-forge does not
implement PKCS#7 verification, so it reports every bundle as unsigned. This checks the same signature with
`openssl cms -verify`, trusting only the given certificate. Exits 0 when the signature is valid, 1 otherwise.
"""
import os
import struct
import subprocess
import sys
import tempfile

HEADER, FOOTER = b"MCPB_SIG_V1", b"MCPB_SIG_END"


def main(bundle: str, cert: str) -> int:
    data = open(bundle, "rb").read()
    footer = data.rfind(FOOTER)
    header = data.rfind(HEADER, 0, footer) if footer >= 0 else -1
    if header < 0:
        print(f"{bundle}: no signature block")
        return 1
    (length,) = struct.unpack_from("<I", data, header + len(HEADER))
    start = header + len(HEADER) + 4
    signature, content = data[start : start + length], data[:header]
    with tempfile.TemporaryDirectory() as folder:
        content_path, signature_path = os.path.join(folder, "content"), os.path.join(folder, "signature.der")
        open(content_path, "wb").write(content)
        open(signature_path, "wb").write(signature)
        result = subprocess.run(
            ["openssl", "cms", "-verify", "-binary", "-inform", "DER", "-in", signature_path, "-content", content_path,
             "-CAfile", cert, "-purpose", "any", "-out", os.devnull],
            capture_output=True, text=True,
        )
    print(f"{bundle}: {'signature valid' if result.returncode == 0 else 'signature INVALID'} ({result.stderr.strip()})")
    return 0 if result.returncode == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
