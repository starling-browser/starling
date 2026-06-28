#!/usr/bin/env python3
"""Serve this folder on your local network so a phone can reach it.

Run it on a computer that's on the same Wi-Fi as your phone:

    python3 serve.py          # default port 8099
    python3 serve.py 9000     # pick another port

It prints a http://<your-computer-ip>:<port>/ link. Open that on the phone.
"""

import http.server
import os
import socket
import sys


def lan_ip():
    # Open a UDP socket toward a public address (no packets are sent) so the OS
    # picks the network interface it would use, then read that interface's IP.
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        return s.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        s.close()


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8099
    os.chdir(os.path.dirname(os.path.abspath(__file__)))

    handler = http.server.SimpleHTTPRequestHandler
    httpd = http.server.ThreadingHTTPServer(("0.0.0.0", port), handler)

    ip = lan_ip()
    print("\n  Magnet Alphabet is being served.\n")
    print("  On this computer:   http://localhost:%d/" % port)
    print("  On your phone:      http://%s:%d/" % (ip, port))
    print("\n  (Phone must be on the same Wi-Fi. Ctrl+C to stop.)\n")

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n  Stopped.")


if __name__ == "__main__":
    main()
