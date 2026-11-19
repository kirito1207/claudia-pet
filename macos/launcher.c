/* ~/Applications/claudia-pet.app 里真正的可执行文件：转去执行 ~/.local/share/claudia-pet/macos/claudia-pet。
 * 不直接用 shell 脚本当 .app 的可执行文件：Apple 芯片上那样会走 Rosetta（没装的话还会弹窗让你装）。
 * install.sh 安装时用 cc 编成通用二进制（arm64 + x86_64）。 */
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(int argc, char **argv) {
    const char *home = getenv("HOME");
    char path[4096];
    (void)argc;
    if (home == NULL || home[0] == '\0') return 127;
    if (snprintf(path, sizeof path, "%s/.local/share/claudia-pet/macos/claudia-pet", home) >= (int)sizeof path) return 127;
    argv[0] = path;
    execv(path, argv);
    perror("claudia-pet");
    return 127;
}
