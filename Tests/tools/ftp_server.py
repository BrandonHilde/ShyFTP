"""Run a local FTP server for ShyFTP integration testing.

Usage:
    python Tests/tools/ftp_server.py [root] [port]

Defaults to a fresh temp directory on 127.0.0.1:2121 with user test/test.
"""

import os
import sys
import tempfile

from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else tempfile.mkdtemp(prefix="shyftp-ftp-")
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 2121

    os.makedirs(root, exist_ok=True)

    authorizer = DummyAuthorizer()
    authorizer.add_user("test", "test", root, perm="elradfmwMT")
    authorizer.add_anonymous(root, perm="elr")

    handler = FTPHandler
    handler.authorizer = authorizer
    handler.passive_ports = list(range(51000, 51100))  # type: ignore[assignment]

    server = FTPServer(("127.0.0.1", port), handler)
    print(
        f"shyftp test ftpd on 127.0.0.1:{port} root={root} user=test pass=test",
        flush=True,
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.close_all()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
