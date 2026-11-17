// shl: Claude Code hook・コマンド・ダブルクリック用の CLI．
// v12-α でサブコマンドを入れる．それまでは何もせず 0 で終わる．
// hook（UserPromptSubmit）から呼ばれたとき exit 2 を返すとプロンプトが消えるので，
// 失敗は必ず 0 か 1 で返す．
return 0;
