"""Regression checks for the runner: python -B -m unittest discover -s tools -v."""

import contextlib
import io
import pathlib
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

import mutation_check as runner


class CommandTests(unittest.TestCase):
    def test_interrupt_and_timeout_kill_and_reap_before_propagating(self):
        for error in (KeyboardInterrupt(), subprocess.TimeoutExpired("dotnet", 1)):
            with self.subTest(error=type(error).__name__):
                process = Mock()
                events = []

                def communicate(**kwargs):
                    events.append("communicate")
                    if len(events) == 1:
                        raise error
                    return "", ""

                process.communicate.side_effect = communicate
                with patch.object(runner.subprocess, "Popen", return_value=process), patch.object(
                    runner, "kill_tree", side_effect=lambda p: events.append("kill")
                ) as kill:
                    with self.assertRaises(type(error)):
                        runner.run_command(["dotnet", "test"], timeout=1)
                    kill.assert_called_once_with(process)
                self.assertEqual(events, ["communicate", "kill", "communicate"])

    def test_build_uses_interrupt_safe_command_runner(self):
        with patch.object(runner, "run_command", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.build()

    def test_cleanup_failure_is_not_reported_as_a_detected_timeout(self):
        process = Mock()
        process.communicate.side_effect = subprocess.TimeoutExpired("dotnet", 1)
        with patch.object(runner.subprocess, "Popen", return_value=process), patch.object(
            runner, "kill_tree", side_effect=subprocess.CalledProcessError(1, "taskkill")
        ):
            with self.assertRaises(subprocess.CalledProcessError):
                runner.test()
        process.communicate.assert_called_once()

    def test_timeout_is_detected_but_interrupt_is_propagated(self):
        with patch.object(runner, "run_command", side_effect=subprocess.TimeoutExpired("dotnet", 1)):
            self.assertEqual(runner.test().kind, runner.TIMED_OUT)
        with patch.object(runner, "run_command", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                runner.test()

    def test_test_results_keep_existing_verdicts(self):
        for output, code, expected in (
            ("Passed! - Failed: 0, Passed: 170", 0, runner.TESTED),
            ("Failed! - Failed: 1, Passed: 169", 1, runner.TESTED),
            ("Test Run Aborted.", 1, runner.RUN_FAILED),
            ("Passed! - Failed: 0, Passed: 170", 1, runner.RUN_FAILED),
        ):
            with self.subTest(output=output, code=code), patch.object(
                runner, "run_command", return_value=subprocess.CompletedProcess([], code, output, "")
            ):
                self.assertEqual(runner.test().kind, expected)


class RestorationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        self.source = self.root / "Example.cs"
        # Both UTF-8 text and LF endings must survive on Windows too.
        self.original = "// caf\u00e9\noriginal\n".encode("utf-8")
        self.source.write_bytes(self.original)
        self.mutation = runner.Mutation("DEMO", "probe", "Example.cs", "original", "mutated")
        for name, value in (("API", self.root), ("MUTATIONS", [self.mutation])):
            patcher = patch.object(runner, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.output = io.StringIO()
        redirect = contextlib.redirect_stdout(self.output)
        redirect.__enter__()
        self.addCleanup(redirect.__exit__, None, None, None)

    def build_result(self, code=0):
        return subprocess.CompletedProcess([], code, "build diagnostic\n" if code else "", "")

    def test_final_build_failure_fails_the_run_and_prints_diagnostics(self):
        with patch.object(runner, "build", side_effect=[self.build_result(), self.build_result(), self.build_result(1)]), patch.object(
            runner, "test", side_effect=[runner.TestRun(runner.TESTED, passed=170), runner.TestRun(runner.TESTED, failed=1)]
        ):
            self.assertEqual(runner.main([]), 1)
        self.assertIn("Restored-source build failed", self.output.getvalue())
        self.assertIn("build diagnostic", self.output.getvalue())
        self.assertEqual(self.source.read_bytes(), self.original)

    def test_success_restores_exact_source_bytes_before_final_build(self):
        observed = []

        def build():
            observed.append(self.source.read_bytes())
            return self.build_result()

        with patch.object(runner, "build", side_effect=build), patch.object(
            runner, "test", side_effect=[runner.TestRun(runner.TESTED, passed=170), runner.TestRun(runner.TESTED, failed=1)]
        ):
            self.assertEqual(runner.main([]), 0)
        self.assertEqual(observed[0], self.original)
        self.assertIn(b"mutated", observed[1])
        self.assertEqual(observed[2], self.original)

    def test_interrupted_test_restores_source_and_rebuilds(self):
        with patch.object(runner, "build", return_value=self.build_result()) as build, patch.object(
            runner, "test", side_effect=[runner.TestRun(runner.TESTED, passed=170), KeyboardInterrupt()]
        ):
            with self.assertRaises(KeyboardInterrupt):
                runner.main([])
        self.assertEqual(build.call_count, 3)
        self.assertEqual(self.source.read_bytes(), self.original)

    def test_interrupted_mutation_write_restores_source_and_rebuilds(self):
        def interrupted_write(path, text, **kwargs):
            path.write_bytes(b"partially written mutation")
            raise KeyboardInterrupt

        with patch.object(runner, "build", return_value=self.build_result()) as build, patch.object(
            runner, "test", return_value=runner.TestRun(runner.TESTED, passed=170)
        ), patch.object(pathlib.Path, "write_text", interrupted_write):
            with self.assertRaises(KeyboardInterrupt):
                runner.main([])
        self.assertEqual(build.call_count, 2)
        self.assertEqual(self.source.read_bytes(), self.original)


if __name__ == "__main__":
    unittest.main()
