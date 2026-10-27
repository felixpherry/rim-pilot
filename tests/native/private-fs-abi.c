#define _GNU_SOURCE
#include <dirent.h>
#include <fcntl.h>
#include <stddef.h>
#include <stdio.h>
#include <sys/stat.h>
#include <sys/types.h>

#if !defined(__linux__) || !defined(__x86_64__) || !defined(__GLIBC__)
#error "Private filesystem ABI requires Linux x86-64 glibc."
#endif

/* Independent public-header check for the C# explicit struct/PInvoke boundary.
   No game headers, native library distribution or copied library implementation. */
_Static_assert(sizeof(void *) == 8 && sizeof(long) == 8, "LP64");
_Static_assert(sizeof(ssize_t) == 8 && sizeof(off_t) == 8, "I/O widths");
_Static_assert(sizeof(uid_t) == 4 && sizeof(mode_t) == 4, "metadata widths");
_Static_assert(sizeof(struct stat) == 144, "stat size");
_Static_assert(offsetof(struct stat, st_dev) == 0, "device");
_Static_assert(offsetof(struct stat, st_ino) == 8, "inode");
_Static_assert(offsetof(struct stat, st_nlink) == 16, "links");
_Static_assert(offsetof(struct stat, st_mode) == 24, "mode");
_Static_assert(offsetof(struct stat, st_uid) == 28, "uid");
_Static_assert(offsetof(struct stat, st_gid) == 32, "gid");
_Static_assert(offsetof(struct stat, st_size) == 48, "size");
_Static_assert(offsetof(struct stat, st_mtim.tv_sec) == 88, "mtime seconds");
_Static_assert(offsetof(struct stat, st_mtim.tv_nsec) == 96, "mtime nanoseconds");
_Static_assert(offsetof(struct stat, st_ctim.tv_sec) == 104, "ctime seconds");
_Static_assert(offsetof(struct stat, st_ctim.tv_nsec) == 112, "ctime nanoseconds");
_Static_assert(offsetof(struct dirent, d_reclen) == 16, "dirent length");
_Static_assert(offsetof(struct dirent, d_name) == 19, "dirent name");
_Static_assert(O_PATH == 2097152 && O_CLOEXEC == 524288 && O_NOFOLLOW == 131072, "open flags");
_Static_assert(O_DIRECTORY == 65536 && O_NONBLOCK == 2048 && O_NOCTTY == 256, "type flags");
_Static_assert(O_CREAT == 64 && O_EXCL == 128 && O_RDWR == 2, "create flags");
_Static_assert(AT_FDCWD == -100 && AT_REMOVEDIR == 512 && RENAME_NOREPLACE == 1, "namespace flags");
_Static_assert(S_IFMT == 61440 && S_IFDIR == 16384 && S_IFREG == 32768, "type bits");
_Static_assert(0700 == 448 && 0600 == 384 && 01777 == 1023, "mode bits");

int main(void) {
    puts("Native ABI: Linux x86-64 glibc layout verified.");
    return 0;
}
