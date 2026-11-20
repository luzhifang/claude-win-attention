#!/bin/sh
# Claude Code hook 入口：WSL 行 win-attention.py；原生 Windows（Git Bash）编译并调用 exe --hook。
# 其他系统（macOS、纯 Linux）由 win-attention.py 自己判断后静默退出；没有 python3 也直接退出。
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    here=$(cd "$(dirname "$(cygpath -u "$0")")" && pwd) || exit 0
    src="$here/win-attention.cs"
    dir="$(cygpath -u "$LOCALAPPDATA")/claude-attention"
    exe="$dir/claude-attention-$(sha1sum "$src" | cut -c1-8).exe"
    if [ ! -f "$exe" ]; then
      # 与 win-attention.py 的 ensure_exe 用同一套参数；按源码哈希命名，源码改了自动重编
      mkdir -p "$dir" && cp "$src" "$dir/win-attention.cs" || exit 0
      net="$(cygpath -u "${WINDIR:-C:\\Windows}")/Microsoft.NET/Framework64/v4.0.30319"
      wpf="$(cygpath -w "$net")\\WPF"
      tmp="$dir/build-$$.exe"
      MSYS2_ARG_CONV_EXCL='*' "$net/csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 \
        "/out:$(cygpath -w "$tmp")" \
        "/r:$wpf\\PresentationFramework.dll" "/r:$wpf\\PresentationCore.dll" "/r:$wpf\\WindowsBase.dll" \
        "/r:$wpf\\UIAutomationClient.dll" "/r:$wpf\\UIAutomationTypes.dll" \
        /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll \
        "$(cygpath -w "$dir/win-attention.cs")" >"$dir/build.log" 2>&1 && mv -f "$tmp" "$exe" || { rm -f "$tmp"; exit 0; }
    fi
    payload="$dir/hook-$$.json"
    cat >"$payload" || exit 0
    exec "$exe" --hook "$(cygpath -w "$payload")" "$@"
    ;;
  *)
    command -v python3 >/dev/null 2>&1 || exit 0
    exec python3 "$(dirname "$0")/win-attention.py" "$@"
    ;;
esac
