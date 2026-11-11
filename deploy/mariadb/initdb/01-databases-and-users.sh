#!/bin/bash
# MiniFactory MariaDB 容器初始化（architecture.md §16、database.md §1）。
# 只在資料目錄為空的第一次啟動時由官方映像的 entrypoint 執行（掛載於 /docker-entrypoint-initdb.d/）。
#
# 建立：
#   database minifactory、minifactory_sim（utf8mb4 / utf8mb4_unicode_ci）
#   minifactory_migrations：DbUp journal（schemaversions）專用 database，只有 mf_migrator 有權限
#   mf_app      ：minifactory 的 SELECT、INSERT、UPDATE、DELETE（無 DDL）；對 minifactory_migrations 沒有任何權限，
#                 所以不能竄改遷移紀錄。journal 之所以獨立成一個 database：MariaDB 權限只能累加，
#                 無法「db 層級授權、但排除某張表」，且 initdb 執行時業務表與 schemaversions 都還不存在，無法逐表授權
#   mf_migrator ：minifactory 的 DDL + DML、minifactory_migrations 的 DDL + DML（只給 migrator 服務使用）
#   mf_sim      ：minifactory_sim 的 DDL + DML；對 minifactory 沒有任何權限
#
# 密碼一律來自環境變數，不寫死；腳本不輸出任何密碼。
# 本檔可被 entrypoint「執行」或「source」，所以全部邏輯放在子 shell，不改變呼叫端的 shell 選項。

(
  set -eu

  # ---- 必要環境變數 ----
  : "${MARIADB_ROOT_PASSWORD:?缺少 MARIADB_ROOT_PASSWORD}"
  : "${MF_APP_USER:?缺少 MF_APP_USER}"
  : "${MF_APP_PASSWORD:?缺少 MF_APP_PASSWORD}"
  : "${MF_MIGRATOR_USER:?缺少 MF_MIGRATOR_USER}"
  : "${MF_MIGRATOR_PASSWORD:?缺少 MF_MIGRATOR_PASSWORD}"
  : "${MF_SIM_USER:?缺少 MF_SIM_USER}"
  : "${MF_SIM_PASSWORD:?缺少 MF_SIM_PASSWORD}"

  # 帳號允許的來源主機。預設 '%'（容器網路與本機埠轉發的來源位址會隨環境變動，
  # 主機埠已在 compose 限定只綁 127.0.0.1）；需要時可用 MF_DB_USER_HOST 收斂，例如 172.30.0.%
  USER_HOST="${MF_DB_USER_HOST:-%}"

  # ---- 輸入檢查：帳號與主機只允許安全字元，避免拼進 SQL 時被注入 ----
  require_identifier() {
    case "$2" in
      '' | *[!A-Za-z0-9_]*) echo "initdb：$1 只能包含英數字與底線" >&2; exit 1 ;;
    esac
  }
  require_identifier MF_APP_USER "$MF_APP_USER"
  require_identifier MF_MIGRATOR_USER "$MF_MIGRATOR_USER"
  require_identifier MF_SIM_USER "$MF_SIM_USER"
  case "$USER_HOST" in
    '' | *[!A-Za-z0-9_.%-]*) echo "initdb：MF_DB_USER_HOST 含不允許的字元" >&2; exit 1 ;;
  esac

  # 密碼放進 SQL 字串前跳脫反斜線與單引號
  sql_escape() {
    local bs='\' sq="'"
    local v=${1//"$bs"/"$bs$bs"}
    printf '%s' "${v//"$sq"/"$bs$sq"}"
  }
  APP_PW=$(sql_escape "$MF_APP_PASSWORD")
  MIGRATOR_PW=$(sql_escape "$MF_MIGRATOR_PASSWORD")
  SIM_PW=$(sql_escape "$MF_SIM_PASSWORD")

  # 以 root 經 unix socket 連線（entrypoint 暫時伺服器沒有開網路）；密碼走環境變數，不出現在命令列
  export MYSQL_PWD="$MARIADB_ROOT_PASSWORD"
  socket_args=()
  if [ -n "${SOCKET:-}" ]; then
    socket_args=(--socket="$SOCKET")
  fi

  mariadb --protocol=socket -uroot "${socket_args[@]}" <<SQL
CREATE DATABASE IF NOT EXISTS \`minifactory\`     CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS \`minifactory_sim\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS \`minifactory_migrations\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

CREATE OR REPLACE USER '${MF_APP_USER}'@'${USER_HOST}'      IDENTIFIED BY '${APP_PW}';
CREATE OR REPLACE USER '${MF_MIGRATOR_USER}'@'${USER_HOST}' IDENTIFIED BY '${MIGRATOR_PW}';
CREATE OR REPLACE USER '${MF_SIM_USER}'@'${USER_HOST}'      IDENTIFIED BY '${SIM_PW}';

GRANT SELECT, INSERT, UPDATE, DELETE ON \`minifactory\`.* TO '${MF_APP_USER}'@'${USER_HOST}';
GRANT CREATE, ALTER, DROP, INDEX, REFERENCES, SELECT, INSERT, UPDATE, DELETE ON \`minifactory\`.* TO '${MF_MIGRATOR_USER}'@'${USER_HOST}';
GRANT CREATE, ALTER, DROP, INDEX, SELECT, INSERT, UPDATE, DELETE ON \`minifactory_migrations\`.* TO '${MF_MIGRATOR_USER}'@'${USER_HOST}';
GRANT CREATE, ALTER, DROP, INDEX, REFERENCES, SELECT, INSERT, UPDATE, DELETE ON \`minifactory_sim\`.* TO '${MF_SIM_USER}'@'${USER_HOST}';
SQL

  echo "initdb：已建立 minifactory / minifactory_sim / minifactory_migrations 與 ${MF_APP_USER} / ${MF_MIGRATOR_USER} / ${MF_SIM_USER}（host ${USER_HOST}）"
)
