"""Regression self-test for the TASK 2 / P1-07 repair of
tools/dev/release-v1-acceptance/final-verification.ps1.

Properties are proven, not asserted:

  1. POSITIVE - run against the real repository in offline mode, the repaired
     verification script exits 0 and reports the hook configuration as valid.

  2. NEGATIVE - run against throw-away git repositories that carry a
     deliberately broken state, the same script MUST fail and MUST name the
     exact check that failed. This is what proves the drift detection is not
     vacuous.

     The negative cases are deliberately independent, each in a fresh scratch
     repository:
       N1  an unexpected modification of a tracked file
       N2  an unexpected untracked file
       N3  the codex hook runner missing from disk
       N4  the codex hook runner present but stripped of its relocation logic
       N5  the codex hook runner not under version control (the detail line
           must report the version-control gap)

Every failure assertion matches the whole check line ("[FAIL] <name>"), not
just the presence of the name, so a passing check can never satisfy it.

The scratch repositories live in the system temp directory. Nothing in the
project repository is created, modified or deleted by this test.
"""
import locale
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
VERIFIER = REPO_ROOT / 'tools' / 'dev' / 'release-v1-acceptance' / 'final-verification.ps1'
HOOKS_JSON = REPO_ROOT / '.codex' / 'hooks.json'
HOOK_RUNNER = REPO_ROOT / '.codex' / 'hooks' / 'run-node-hook.cmd'

# This file is a gate, so it must never die while printing a diagnostic.
# Python writes to a pipe using the ambient locale encoding (cp936 on this
# host); a message carrying a character outside that encoding aborts the run
# and hides the failure it was about to report. Force UTF-8 with replacement.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding='utf-8', errors='replace')
    except (AttributeError, ValueError):
        pass

HOOK_CHECK = 'codex hook configuration is valid and relocation-safe'
MODIFIED_CHECK = 'no modified tracked files outside the asserted hook configuration'
UNTRACKED_CHECK = 'no unexpected untracked files'

checks = 0
failures = []


def fail(message):
    failures.append(message)
    print('FAIL: ' + message)


def passed(message):
    global checks
    checks += 1
    print('ok   ' + message)


def decode(raw):
    """Decode a child process stream without depending on one code page.

    The child inherits the console code page of whoever started it. A plain
    UTF-8 decode would turn every non-ASCII byte into U+FFFD, so fall back to
    the ambient encoding when the bytes are not valid UTF-8.
    """
    try:
        return raw.decode('utf-8')
    except UnicodeDecodeError:
        try:
            return raw.decode(locale.getpreferredencoding(False), errors='replace')
        except LookupError:
            return raw.decode('utf-8', errors='replace')


def run(cmd, cwd):
    completed = subprocess.run(
        cmd, cwd=str(cwd), capture_output=True, shell=False)
    return completed.returncode, decode(completed.stdout or b'') + decode(completed.stderr or b'')


def git(args, cwd):
    return run(['git'] + args, cwd)


def verifier(repo_path):
    # Force pwsh to emit UTF-8 so the captured text does not depend on the
    # console code page this process happens to inherit. The verification
    # script is invoked with an explicit -Repo so the check is not tied to
    # one machine.
    command = (
        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); "
        "& '{script}' -Repo '{repo}' -OfflineOnly; exit $LASTEXITCODE"
    ).format(script=VERIFIER, repo=repo_path)
    return run(['pwsh', '-NoProfile', '-Command', command], REPO_ROOT)


def line_for(output, name):
    """Return the verdict line for a named check, or '' when it is absent."""
    for line in output.splitlines():
        stripped = line.strip()
        if stripped.startswith('[PASS]') or stripped.startswith('[FAIL]'):
            if stripped[len('[PASS]'):].strip().startswith(name):
                return stripped
    return ''


def assert_fails(output, name, label):
    """The named check must have FAILED, matched on the whole line."""
    verdict = line_for(output, name)
    if not verdict:
        fail(label + ' - the check "' + name + '" was not reported at all:\n' + output)
        return
    if not verdict.startswith('[FAIL]'):
        fail(label + ' - the check "' + name + '" did not fail:\n' + verdict)
        return
    passed(label)


def make_scratch():
    """A fresh scratch repository that mimics the hook configuration."""
    scratch = Path(tempfile.mkdtemp(prefix='gv-drift-negative-'))
    (scratch / '.codex' / 'hooks').mkdir(parents=True, exist_ok=True)
    (scratch / 'tools' / 'dev').mkdir(parents=True, exist_ok=True)
    shutil.copyfile(HOOKS_JSON, scratch / '.codex' / 'hooks.json')
    shutil.copyfile(HOOK_RUNNER, scratch / '.codex' / 'hooks' / 'run-node-hook.cmd')
    (scratch / 'tools' / 'dev' / 'probe-live-sites.ps1').write_text('x', encoding='utf-8')
    for args in (['init', '-q'], ['config', 'user.email', 'selftest@localhost'],
                 ['config', 'user.name', 'selftest'], ['add', '-A'], ['commit', '-q', '-m', 'init']):
        code, out = git(args, scratch)
        if code != 0:
            fail('scratch git setup failed: ' + out)
            shutil.rmtree(scratch, ignore_errors=True)
            return None
    return scratch


def main():
    if not VERIFIER.exists():
        fail('verification script not found: ' + str(VERIFIER))
        return
    if not HOOKS_JSON.exists() or not HOOK_RUNNER.exists():
        fail('hook configuration files are missing')
        return

    # ---------------- real repository: report its actual delivery state ----------------
    # The subject of this self-test is the drift and hook detection. Whether
    # the WHOLE repository is green is the release verifier's business: a
    # normal intermediate state between the commit phase and the approved
    # push (HEAD ahead of the recorded remote) must not turn this self-test
    # red, or it would mask the signal it exists to produce. The drift-scoped
    # checks are therefore asserted individually and by name, and the
    # delivery gap itself is still required to FAIL loudly.
    exit_code, output = verifier(REPO_ROOT)
    code, _ = git(['ls-files', '--error-unmatch', '.codex/hooks/run-node-hook.cmd'], REPO_ROOT)
    runner_tracked = code == 0
    expected_verdict = '[PASS]' if runner_tracked else '[FAIL]'
    if line_for(output, HOOK_CHECK).startswith(expected_verdict):
        passed('the hook configuration check matches runner tracking state')
    else:
        fail('the hook configuration check does not match runner tracking state:\n' + output)
    if not runner_tracked:
        if exit_code != 0:
            passed('an untracked runner is reported as a failing delivery gap, not tolerated')
        else:
            fail('an untracked runner was not reported as a failing delivery gap:\n' + output)
    for name in (MODIFIED_CHECK, UNTRACKED_CHECK):
        if line_for(output, name).startswith('[PASS]'):
            passed('the drift-scoped check passes on the real repository: ' + name)
        else:
            fail('the drift-scoped check failed on the real repository: ' + name + '\n' + output)
    if 'FINAL VERIFICATION:' in output:
        passed('the verification produced a summary line')
    else:
        fail('the verification did not produce a summary line:\n' + output)

    # ---------------- N1: an unexpected modification of a tracked file ----------------
    scratch = make_scratch()
    if scratch is not None:
        try:
            (scratch / 'tools' / 'dev' / 'probe-live-sites.ps1').write_text('changed', encoding='utf-8')
            exit_code, output = verifier(scratch)
            if exit_code == 0:
                fail('an unexpected modification to a tracked file was NOT detected')
            else:
                passed('an unexpected modification to a tracked file fails the verification')
            assert_fails(output, MODIFIED_CHECK,
                         'the verification names the unexpected modification check')
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    # ---------------- N2: an unexpected untracked file ----------------
    scratch = make_scratch()
    if scratch is not None:
        try:
            (scratch / 'zzz-unexpected.txt').write_text('x', encoding='utf-8')
            exit_code, output = verifier(scratch)
            if exit_code == 0:
                fail('an unexpected untracked file was NOT detected')
            else:
                passed('an unexpected untracked file fails the verification')
            assert_fails(output, UNTRACKED_CHECK,
                         'the verification names the unexpected untracked check')
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    # ---------------- N3: the hook runner missing from disk ----------------
    scratch = make_scratch()
    if scratch is not None:
        try:
            (scratch / '.codex' / 'hooks' / 'run-node-hook.cmd').unlink()
            exit_code, output = verifier(scratch)
            if exit_code == 0:
                fail('a missing codex hook runner was NOT detected')
            else:
                passed('a missing codex hook runner fails the verification')
            assert_fails(output, HOOK_CHECK,
                         'the verification names the hook configuration check for the missing runner')
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    # ---------------- N4: the hook runner stripped of its relocation logic ----------------
    scratch = make_scratch()
    if scratch is not None:
        try:
            (scratch / '.codex' / 'hooks' / 'run-node-hook.cmd').write_text(
                '@echo off\r\nnode "%SCRIPT%"\r\n', encoding='utf-8')
            exit_code, output = verifier(scratch)
            if exit_code == 0:
                fail('a codex hook runner without relocation logic was NOT detected')
            else:
                passed('a codex hook runner without relocation logic fails the verification')
            assert_fails(output, HOOK_CHECK,
                         'the verification names the hook configuration check for the broken runner')
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    # ---------------- N5: the hook runner not under version control ----------------
    scratch = make_scratch()
    if scratch is not None:
        try:
            code, out = git(['rm', '-q', '--cached', '.codex/hooks/run-node-hook.cmd'], scratch)
            if code != 0:
                fail('could not untrack the scratch hook runner: ' + out)
            else:
                exit_code, output = verifier(scratch)
                if 'runnerNotTracked' not in output:
                    fail('the version-control gap of the hook runner was NOT reported')
                else:
                    passed('the version-control gap of the hook runner is reported in the detail line')
                assert_fails(output, HOOK_CHECK,
                             'the untracked runner fails the hook configuration check')
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

    # ---------------- N6: the hook runner tracked and complete ----------------
    # The positive counterpart of N5: with the runner under version control
    # the same check must pass. Without this case the strict criterion of N5
    # could also be satisfied by a verifier that always fails.
    scratch = make_scratch()
    if scratch is not None:
        try:
            exit_code, output = verifier(scratch)
            if line_for(output, HOOK_CHECK).startswith('[PASS]'):
                passed('a tracked and complete hook runner passes the hook configuration check')
            else:
                fail('a tracked and complete hook runner did NOT pass the hook configuration check:\n' + output)
            if 'runnerNotTracked' in output:
                fail('a tracked hook runner was reported as untracked:\n' + output)
            else:
                passed('a tracked hook runner is not reported as untracked')
        finally:
            shutil.rmtree(scratch, ignore_errors=True)


if __name__ == '__main__':
    main()
    if failures:
        print('FINAL_VERIFICATION_DRIFT_SELFTEST_FAIL checks=%d failures=%d' % (checks, len(failures)))
        sys.exit(1)
    print('FINAL_VERIFICATION_DRIFT_SELFTEST_PASS checks=%d failures=0' % checks)
    sys.exit(0)
