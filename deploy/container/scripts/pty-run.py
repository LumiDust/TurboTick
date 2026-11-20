#!/usr/bin/env python3
"""Runs a command under a pseudo-terminal and forwards its output unbuffered.

Why this exists (measured on the deployment host, 2026-11-16)
------------------------------------------------------------
`steamcmd +login <account>` asks for the password on stdin and asks for a Steam Guard code the same
way. Reading its output through a plain pipe never shows either prompt: the child's stdout is
block-buffered because it is not a tty, so everything - `password:`, `ERROR (Invalid Password)`,
`Steam Guard code:` - is flushed only when the process dies. A reader therefore waits three
minutes, sees nothing, and reports a timeout; the child, meanwhile, waits forever for a line that
the reader would have written had it seen the prompt. Two `script(1)` variants were tried and both
still buffered, because script()'s own stdout was a pipe.

What actually works is owning the pty: the child gets a pty slave (so a C program switches to line
buffering and its prompts arrive immediately), and this process reads the pty master with os.read,
which has no buffering of its own. stdin is forwarded byte-for-byte to the pty, so the password and
the Steam Guard code reach steamcmd as ordinary typed input.

The second half of the problem: **a prompt is not a line.** steamcmd writes `password: ` with a
trailing space and *no newline*, then blocks on read(). A reader that only emits complete lines holds
that prompt forever and the two sides deadlock the same way as before. So anything still pending when
the stream goes quiet for IDLE_FLUSH_SECONDS is emitted as its own line; the newline this adds is the
only one the reader needs.

Usage: pty-run.py -- COMMAND [ARGS...]

Output goes to fd 1 with CRLF collapsed to LF and SGR colour codes kept (the caller strips them).
The child's exit status becomes this process's exit status.
"""
import os
import pty
import select
import signal
import sys
import termios
import time

# How long the stream has to be silent before a partial line is published. A prompt arrives and the
# child immediately blocks on read(), so silence is the signal that nothing more is coming.
IDLE_FLUSH_SECONDS = 0.2


def main() -> int:
    argv = sys.argv[1:]
    if argv and argv[0] == "--":
        argv = argv[1:]
    if not argv:
        sys.stderr.write("pty-run: no command\n")
        return 2

    pid, master = pty.fork()
    if pid == 0:
        # The pty echoes what is written to it, which would put the Steam password and the Steam Guard
        # code into the transcript the page shows. Turning ECHO off before exec is what keeps them out
        # of the log: the line still reaches the child's read(), it just is not echoed back.
        try:
            attributes = termios.tcgetattr(0)
            attributes[3] &= ~termios.ECHO
            termios.tcsetattr(0, termios.TCSANOW, attributes)
        except (termios.error, OSError):
            pass
        try:
            os.execvp(argv[0], argv)
        except OSError as error:
            sys.stderr.write("pty-run: cannot exec %s: %s\n" % (argv[0], error))
        os._exit(127)

    stdin_fd = sys.stdin.fileno()
    out = sys.stdout.buffer
    pending = b""
    stdin_open = True
    status = 0

    def publish(text: bytes) -> None:
        out.write(text.replace(b"\r", b"") + b"\n")
        out.flush()

    def drain_lines() -> None:
        nonlocal pending
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            publish(line)

    last_data = time.monotonic()
    try:
        while True:
            watch = [master]
            if stdin_open:
                watch.append(stdin_fd)
            try:
                ready, _, _ = select.select(watch, [], [], 0.05)
            except InterruptedError:
                continue
            if stdin_fd in ready:
                try:
                    chunk = os.read(stdin_fd, 4096)
                except OSError:
                    chunk = b""
                if chunk:
                    os.write(master, chunk)
                else:
                    # EOF on our stdin means the caller is done talking. Signal it as ^D rather than
                    # closing the pty, which would kill the child instead of answering it.
                    stdin_open = False
                    try:
                        os.write(master, b"\x04")
                    except OSError:
                        pass
            if master in ready:
                try:
                    data = os.read(master, 4096)
                except OSError:
                    data = b""
                if not data:
                    break
                pending += data
                last_data = time.monotonic()
                drain_lines()
            elif pending and time.monotonic() - last_data >= IDLE_FLUSH_SECONDS:
                # A prompt with no newline: publish it so the caller's reader stops waiting for one.
                publish(pending)
                pending = b""
                last_data = time.monotonic()
            done, status = os.waitpid(pid, os.WNOHANG)
            if done == pid:
                while True:
                    try:
                        data = os.read(master, 4096)
                    except OSError:
                        break
                    if not data:
                        break
                    pending += data
                break
    finally:
        if pending:
            publish(pending)
        try:
            os.close(master)
        except OSError:
            pass
        try:
            done, status = os.waitpid(pid, 0)
        except ChildProcessError:
            pass

    if os.WIFEXITED(status):
        return os.WEXITSTATUS(status)
    if os.WIFSIGNALED(status):
        return 128 + os.WTERMSIG(status)
    return 0


if __name__ == "__main__":
    signal.signal(signal.SIGPIPE, signal.SIG_DFL)
    sys.exit(main())
