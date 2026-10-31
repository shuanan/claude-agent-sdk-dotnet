#!/usr/bin/env bash
# 在本機跑 .github/workflows/ci.yml 的 ubuntu-latest 那一條，加上 release.yml 的打包（只驗證，不上傳）。
# GitHub Actions 不能用時拿它代替 CI：建置、測試、打包都過才算綠燈。
# 用法：tools/local-ci.sh（在 repo 的任何位置都可以）
#
# 跑不到的部分：CI 的 windows-latest 與 macos-latest 兩條只能在那兩個平台上跑，這裡只有 Linux；
# release.yml 的 tag 與版本檢查、NuGet 登入與上傳也不在這裡。
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# GitHub Actions 會設 CI=true，csproj 因此開 ContinuousIntegrationBuild；這裡照做，除非呼叫端自己設了
export CI="${CI:-true}"
# 不送遙測、不印歡迎訊息，跟 CI 的輸出一樣乾淨
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

solution="PamisWang.ClaudeAgentSdk.slnx"
pack_dir="$(mktemp -d "${TMPDIR:-/tmp}/local-ci-pack.XXXXXX")"
trap 'rm -rf "$pack_dir"' EXIT

declare -a step_names=()
declare -a step_results=()
failed=0

record() {
    step_names+=("$1")
    step_results+=("$2")
    if [ "$2" = "FAIL" ]; then
        failed=1
    fi
}

# 跑一個步驟；前面有步驟失敗就跳過（後面的步驟都靠前面的輸出，例如 --no-build）
run_step() {
    local name="$1"
    shift
    if [ "$failed" -ne 0 ]; then
        record "$name" "SKIP"
        return
    fi

    echo
    echo "==> $name"
    echo "    $*"
    if "$@"; then
        record "$name" "PASS"
    else
        record "$name" "FAIL"
    fi
}

# 1. SDK 版本要符合 global.json（version + rollForward），跟 setup-dotnet 讀 global-json-file 一樣
check_sdk() {
    local wanted roll actual
    wanted="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' global.json | head -n 1)"
    roll="$(sed -n 's/.*"rollForward"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' global.json | head -n 1)"
    roll="${roll:-latestPatch}"
    if [ -z "$wanted" ]; then
        echo "global.json 裡找不到 sdk.version"
        return 1
    fi

    if ! actual="$(dotnet --version 2>&1)"; then
        echo "dotnet --version 失敗（多半是沒有符合 global.json 的 SDK）："
        echo "$actual"
        return 1
    fi

    echo "dotnet --version：$actual"
    echo "global.json：version $wanted，rollForward $roll"

    # 版本拆成 major.minor.band.patch；10.0.403 是 feature band 4、patch 03。預覽版的 -preview 後綴不比
    local w_major w_minor w_third a_major a_minor a_third
    IFS=. read -r w_major w_minor w_third <<<"${wanted%%-*}"
    IFS=. read -r a_major a_minor a_third <<<"${actual%%-*}"
    local w_band=$((10#$w_third / 100)) a_band=$((10#$a_third / 100))
    local ok=0
    case "$roll" in
        disable)
            [ "$actual" = "$wanted" ] && ok=1 ;;
        patch | latestPatch)
            [ "$a_major" -eq "$w_major" ] && [ "$a_minor" -eq "$w_minor" ] && [ "$a_band" -eq "$w_band" ] \
                && [ "$((10#$a_third))" -ge "$((10#$w_third))" ] && ok=1 ;;
        feature | latestFeature)
            [ "$a_major" -eq "$w_major" ] && [ "$a_minor" -eq "$w_minor" ] \
                && [ "$((10#$a_third))" -ge "$((10#$w_third))" ] && ok=1 ;;
        minor | latestMinor)
            [ "$a_major" -eq "$w_major" ] \
                && { [ "$a_minor" -gt "$w_minor" ] || { [ "$a_minor" -eq "$w_minor" ] && [ "$((10#$a_third))" -ge "$((10#$w_third))" ]; }; } \
                && ok=1 ;;
        major | latestMajor)
            [ "$(printf '%s\n%s\n' "${wanted%%-*}" "${actual%%-*}" | sort -V | head -n 1)" = "${wanted%%-*}" ] && ok=1 ;;
        *)
            echo "不認得的 rollForward：$roll"
            return 1 ;;
    esac

    if [ "$ok" -ne 1 ]; then
        echo "SDK $actual 不符合 global.json 的 $wanted（rollForward $roll），請安裝符合的 .NET SDK。"
        return 1
    fi
}

run_step "SDK 版本符合 global.json" check_sdk

# 2. 建置，跟 CI 一樣；TreatWarningsAsErrors 來自 Directory.Build.props。
#    CI 每次都是全新的 checkout，這裡加 --no-incremental，免得舊的 obj 讓警告沒有重新出現
run_step "dotnet build" dotnet build "$solution" -c Release --no-incremental

# 3. 測試，跟 CI 一樣用上一步的建置結果
run_step "dotnet test" dotnet test "$solution" -c Release --no-build

# 4. 打包，跟 release.yml 一樣；輸出到暫存資料夾，只確認打得出來，結束時刪掉
pack() {
    dotnet pack src/PamisWang.ClaudeAgentSdk -c Release --no-build -o "$pack_dir"
    local packages
    packages="$(find "$pack_dir" -maxdepth 1 -name '*.nupkg' -print)"
    if [ -z "$packages" ]; then
        echo "沒有產生 .nupkg"
        return 1
    fi

    echo "產生的套件："
    find "$pack_dir" -maxdepth 1 \( -name '*.nupkg' -o -name '*.snupkg' \) -printf '    %f\n'
}

run_step "dotnet pack（只驗證）" pack

# 5. 摘要
echo
echo "==> 摘要"
echo "commit：$(git rev-parse HEAD 2>/dev/null || echo '不明')"
echo "branch：$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '不明')"
if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
    echo "工作目錄有未提交的變更（結果不完全對應上面的 commit）"
fi
for i in "${!step_names[@]}"; do
    printf '  %-4s  %s\n' "${step_results[$i]}" "${step_names[$i]}"
done
echo "沒有跑：CI 的 windows-latest、macos-latest（只能在那兩個平台上跑）"

if [ "$failed" -ne 0 ]; then
    echo "結果：FAIL"
    exit 1
fi

echo "結果：PASS"
