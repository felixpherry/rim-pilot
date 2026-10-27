import pathlib
import subprocess
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]


class NativeFilesystemTests(unittest.TestCase):
    def test_installed_public_headers_match_native_abi(self):
        with tempfile.TemporaryDirectory(prefix="rim-pilot-abi-") as temporary:
            binary = pathlib.Path(temporary) / "abi"
            subprocess.run(["cc", "-std=c11", "-Wall", "-Wextra", "-Werror",
                            str(ROOT / "tests/native/private-fs-abi.c"), "-o", str(binary)],
                           check=True, capture_output=True, timeout=30)
            result = subprocess.run([str(binary)], check=True, capture_output=True, text=True, timeout=5)
            self.assertEqual("Native ABI: Linux x86-64 glibc layout verified.\n", result.stdout)

    def test_boundary_uses_no_managed_path_io_or_permission_repair(self):
        source = (ROOT / "shared/Protocol/LinuxPrivateDirectory.cs").read_text()
        for forbidden in ("FileStream", "DirectorySecurity", "FileSecurity", "chmod(",
                          "Directory.Create", "Directory.Delete", "File.Delete", "File.Move"):
            self.assertNotIn(forbidden, source)
        self.assertIn('private const string Libc = "libc";', source)
        self.assertIn("SafeHandleMinusOneIsInvalid", source)
        self.assertIn("partial void Observe(string point);", source)
        # The test hook's implementation lives only in the test project, not product sources.
        self.assertNotIn("TestHook", source)


if __name__ == "__main__":
    unittest.main()
