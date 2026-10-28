"""Sign the catalog the app refreshes from (PB70).

Writes <catalog>.sig beside catalog.json: the base64 of an ECDSA P-256 SHA-256 signature over
the file's exact bytes, in the IEEE P1363 form (r || s, 64 bytes) Revelei.Inference's
ModelCatalog verifies. The app takes a fetched catalog only when this verifies against the public
key it was built with, so the private key never enters the repository.

    .venv\\Scripts\\python scripts\\sign-catalog.py --new-key %USERPROFILE%\\.revelei\\catalog-key.pem
    .venv\\Scripts\\python scripts\\sign-catalog.py --key %USERPROFILE%\\.revelei\\catalog-key.pem .cache\\publish\\catalog.json

--new-key writes a private key that does not exist yet and prints the public half, base64
SubjectPublicKeyInfo, for ModelCatalog's compiled-in key. Needs `cryptography`
(scripts/requirements.txt).
"""

from __future__ import annotations

import argparse
import base64
import sys
from pathlib import Path

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature, encode_dss_signature

FIELD = 32  # P-256: r and s are 32 bytes each in the P1363 form.


def new_key(path: Path) -> str:
    """Write a new private key to `path`, which must not exist, and return the public half."""
    if path.exists():
        raise FileExistsError(f"{path} already exists; a signing key is never overwritten")
    key = ec.generate_private_key(ec.SECP256R1())
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    )
    return public_key(key)


def public_key(key: ec.EllipticCurvePrivateKey) -> str:
    """The key's public half as the app compiles it in: base64 SubjectPublicKeyInfo."""
    spki = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    return base64.b64encode(spki).decode("ascii")


def load(path: Path) -> ec.EllipticCurvePrivateKey:
    key = serialization.load_pem_private_key(path.read_bytes(), password=None)
    if not isinstance(key, ec.EllipticCurvePrivateKey) or key.curve.name != "secp256r1":
        raise ValueError(f"{path} is not a P-256 private key")
    return key


def sign(key: ec.EllipticCurvePrivateKey, data: bytes) -> str:
    """The signature the app verifies, as catalog.json.sig holds it."""
    r, s = decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    return base64.b64encode(r.to_bytes(FIELD, "big") + s.to_bytes(FIELD, "big")).decode("ascii")


def verify(spki: str, data: bytes, signature: str) -> bool:
    """Whether `signature` verifies over `data` against the base64 SubjectPublicKeyInfo `spki`."""
    key = serialization.load_der_public_key(base64.b64decode(spki))
    raw = base64.b64decode(signature)
    if len(raw) != 2 * FIELD:
        return False
    der = encode_dss_signature(int.from_bytes(raw[:FIELD], "big"), int.from_bytes(raw[FIELD:], "big"))
    try:
        key.verify(der, data, ec.ECDSA(hashes.SHA256()))
    except InvalidSignature:
        return False
    return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Sign catalog.json, or make the signing key.")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--key", type=Path, help="the private key (PEM) to sign with")
    group.add_argument("--new-key", type=Path, help="write a new private key here and print its public half")
    parser.add_argument("catalog", type=Path, nargs="?", help="catalog.json to sign (with --key)")
    args = parser.parse_args(argv)
    if args.new_key is not None:
        try:
            print(new_key(args.new_key))
        except FileExistsError as error:
            print(f"ERROR {error}", file=sys.stderr)
            return 1
        return 0
    if args.catalog is None:
        parser.error("--key needs the catalog.json to sign")
    key = load(args.key)
    signature = sign(key, args.catalog.read_bytes())
    target = args.catalog.with_name(args.catalog.name + ".sig")
    target.write_text(signature + "\n", encoding="ascii", newline="\n")
    print(f"signed {args.catalog} -> {target} (public key {public_key(key)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
