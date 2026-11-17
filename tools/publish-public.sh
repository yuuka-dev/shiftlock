#!/bin/bash
# 開発用リポジトリ（非公開）の中身を、公開用リポジトリの作業ツリーに 1 コミットとして積む。
# push はしない（中身を見てから手で push する）。
#
# 使い方（Git Bash か WSL で、開発用リポジトリの中から）:
#   tools/publish-public.sh [取り出すブランチ（既定 dev）] [公開用の作業ツリー（既定 ../../public/shiftlock）]
#
# やること:
#   1. 公開側の追跡ファイルを全部消し、指定ブランチの中身（git archive）に入れ替える
#   2. 公開しないファイル（EXCLUDE）を外す
#   3. 個人データの名前のファイル・出したくない語（.publish-deny）が無いか確かめる
#   4. 問題なければ公開側で 1 コミットにする。問題があれば公開側を元に戻して止まる
set -euo pipefail

# 公開側に出さないファイル（開発用リポジトリでは追跡している）
EXCLUDE=(
  "pc/shiftlock/i18n/inm.py"
)

# 追跡されていたら止める個人データの名前（CLAUDE.md の「絶対」と同じ）
PERSONAL='(^|/)(shifts\.json|days\.json|state\.json|calendar\.json|learned\.json|events\.log|usage-[^/]*\.json|report\.html|mood\.csv)$|(^|/)(log|reports)/'

die() { echo "中止: $*" >&2; exit 1; }

src=$(git rev-parse --show-toplevel)
ref=${1:-dev}
dest=${2:-$src/../../public/shiftlock}

git -C "$src" rev-parse --verify -q "$ref^{commit}" >/dev/null || die "ブランチ $ref が無い"
[ -e "$dest/.git" ] || die "公開用の作業ツリーが無い: $dest"
dest=$(cd "$dest" && pwd)
[ "$dest" != "$src" ] || die "公開用の作業ツリーが開発用と同じ場所になっている"
[ -z "$(git -C "$dest" status --porcelain)" ] || die "公開側に未コミットの変更がある: $dest"

sha=$(git -C "$src" rev-parse --short "$ref")
tmp=$(mktemp)

# 途中で止まったら公開側を元（HEAD）に戻す。始める前に未コミットの変更が無いことは確かめてある
committed=0
cleanup() {
  rm -f "$tmp"
  if [ "$committed" = 0 ]; then
    git -C "$dest" add -A >/dev/null 2>&1 || true
    git -C "$dest" reset -q --hard HEAD || true
  fi
}
trap cleanup EXIT

# 1. 入れ替え
git -C "$dest" rm -rq --ignore-unmatch .
git -C "$src" archive "$ref" | tar -x -C "$dest"

# 2. 公開しないファイルを外す
for f in "${EXCLUDE[@]}"; do rm -f "$dest/$f"; done
git -C "$dest" add -A

# 3. 確かめる
for f in "${EXCLUDE[@]}"; do
  if git -C "$dest" ls-files --error-unmatch -- "$f" >/dev/null 2>&1; then
    die "公開しないファイルが入っている: $f"
  fi
done
if git -C "$dest" ls-files | grep -E "$PERSONAL"; then
  die "個人データの名前のファイルが入っている（上の一覧）"
fi
deny="$src/.publish-deny"
if [ -f "$deny" ]; then
  # 1 行 1 語（大文字小文字は区別しない）。空行と # で始まる行は読み飛ばす
  tr -d '\r' < "$deny" | grep -vE '^[[:space:]]*(#|$)' > "$tmp" || true
  if [ -s "$tmp" ] && git -C "$dest" grep -nIi -F -f "$tmp" -- .; then
    die "出したくない語が入っている（上の一覧）"
  fi
else
  echo "注意: $deny が無いので、語のチェックは飛ばした" >&2
fi

# 4. コミット
if git -C "$dest" diff --cached --quiet; then
  committed=1
  echo "公開側は $ref（$sha）と同じ中身。コミットするものは無い"
  exit 0
fi
git -C "$dest" commit -q -m "update: 開発版 $sha を取り込み"
committed=1

git -C "$dest" show --stat --format='%h %s' HEAD
echo
echo "公開側にコミットした（push はまだ）。確かめてから:"
echo "  テスト: 公開側の pc/ で python3 -m pytest -q tests（inm.py 無しで通ること）"
echo "  push:   git -C \"$dest\" push origin main"
