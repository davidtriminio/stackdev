#!/usr/bin/env bash
# stackdev-remote.sh - runs ON THE SERVER, as root (StackDev calls it through sudo).
# StackDev uploads a fresh copy for every operation and removes it afterwards; do not edit it by hand.
#
# Subcommands: preflight | discover | prepare | https | deploy | rollback | backups | provision | env-read | env-write
# Output protocol (one item per line, everything else is plain log):
#   ##STEP n/total title      ##INFO text      ##WARN text      ##FAIL code text
#   ##KV key=value            ##API svc|dll|dir|user|state         ##BACKUP ts|date|size_kb
#   ##ENV-BEGIN ... ##ENV-END (the env file, only for env-read)    ##DONE
# Exit codes: 0 ok | 10 bad arguments | 11 preflight failed (nothing changed) | 12 bad upload (nothing changed)
#             20 failed and rolled back | 21 failed and rollback failed | 30 provisioning failed | 31 install failed | 40 env change failed

set -uo pipefail

# If the SSH connection drops in the middle of a change, keep going: stopping halfway could leave the service down.
trap '' HUP PIPE

# Every absolute path goes through $ROOT so the script can be tested in a scratch directory.
ROOT="${SD_ROOT:-}"
APPS="$ROOT/var/www"
DATA="$ROOT/var/lib"
CONF="$ROOT/etc"
BACKUPS="$ROOT/var/backups/stackdev"
UNITS="$ROOT/etc/systemd/system"

STEP_N=0
STEP_TOTAL=0

step() { STEP_N=$((STEP_N + 1)); echo "##STEP $STEP_N/$STEP_TOTAL $*"; }
info() { echo "##INFO $*"; }
warn() { echo "##WARN $*"; }
kv()   { echo "##KV $1=$2"; }
fail() { local code="$1"; shift; echo "##FAIL $code $*"; exit "$code"; }

# ---------------------------------------------------------------- validation
valid_svc()    { [[ "$1" =~ ^[a-z][a-z0-9-]{0,39}$ ]]; }
valid_dll()    { [[ "$1" =~ ^[A-Za-z][A-Za-z0-9._-]*\.dll$ ]]; }
valid_domain() { [[ "$1" =~ ^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$ ]]; }
valid_port()   { [[ "$1" =~ ^[0-9]{2,5}$ ]] && [ "$1" -ge 1024 ] && [ "$1" -le 65535 ]; }
valid_ts()     { [[ "$1" =~ ^[0-9]{8}-[0-9]{6}$ ]]; }
valid_num()    { [[ "$1" =~ ^[0-9]{1,4}$ ]]; }
valid_sha()    { [[ "$1" =~ ^[0-9a-f]{64}$ ]]; }
valid_email()  { [[ "$1" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]]; }
valid_dir()    { [[ "$1" =~ ^/[A-Za-z0-9._/-]+$ ]] && [[ "$1" != *..* ]]; }

# A file passed by the client must live inside the private run directory it uploaded.
inside_run_dir() { case "$1" in "$RUN_DIR"/*) [[ "$1" != *..* ]] ;; *) return 1 ;; esac; }

SVC="" DLL="" RUN_DIR="" BUNDLE="" SHA="" KEEP=5 WAIT=30 RUNTIME="" TO="last"
DOMAIN="" PORT="" WEB="apache" EMAIL="" HTTPS="no" FORCE="no" ENV_FILE_IN="" RESTART="no" DB="" INSTALL="" PW_FILE="" CONTENT=""

CMD="${1:-}"; shift || true
while [ $# -gt 0 ]; do
    case "$1" in
        --svc)      SVC="${2:-}"; shift 2 ;;
        --dll)      DLL="${2:-}"; shift 2 ;;
        --run-dir)  RUN_DIR="${2:-}"; shift 2 ;;
        --bundle)   BUNDLE="${2:-}"; shift 2 ;;
        --sha256)   SHA="${2:-}"; shift 2 ;;
        --keep)     KEEP="${2:-}"; shift 2 ;;
        --wait)     WAIT="${2:-}"; shift 2 ;;
        --runtime)  RUNTIME="${2:-}"; shift 2 ;;
        --to)       TO="${2:-}"; shift 2 ;;
        --domain)   DOMAIN="${2:-}"; shift 2 ;;
        --port)     PORT="${2:-}"; shift 2 ;;
        --web)      WEB="${2:-}"; shift 2 ;;
        --email)    EMAIL="${2:-}"; shift 2 ;;
        --https)    HTTPS="${2:-}"; shift 2 ;;
        --force)    FORCE="yes"; shift ;;
        --env-file) ENV_FILE_IN="${2:-}"; shift 2 ;;
        --pw-file)  PW_FILE="${2:-}"; shift 2 ;;
        --content)  CONTENT="${2:-}"; shift 2 ;;
        --restart)  RESTART="yes"; shift ;;
        --db)       DB="${2:-}"; shift 2 ;;
        --install)  INSTALL="${2:-}"; shift 2 ;;
        *) fail 10 "Unknown argument: $1" ;;
    esac
done

[ -z "$SVC" ] || valid_svc "$SVC" || fail 10 "Invalid service name"
[ -z "$DLL" ] || valid_dll "$DLL" || fail 10 "Invalid dll name"
[ -z "$RUN_DIR" ] || valid_dir "$RUN_DIR" || fail 10 "Invalid run directory"
[ -z "$SHA" ] || valid_sha "$SHA" || fail 10 "Invalid checksum"
[ -z "$CONTENT" ] || valid_sha "$CONTENT" || fail 10 "Invalid content hash"
valid_num "$KEEP" || fail 10 "Invalid --keep"
valid_num "$WAIT" || fail 10 "Invalid --wait"
[ -z "$RUNTIME" ] || valid_num "$RUNTIME" || fail 10 "Invalid --runtime"
[ -z "$DOMAIN" ] || valid_domain "$DOMAIN" || fail 10 "Invalid domain"
[ -z "$PORT" ] || valid_port "$PORT" || fail 10 "Invalid port"
[ -z "$EMAIL" ] || valid_email "$EMAIL" || fail 10 "Invalid email"
[[ "$WEB" =~ ^(apache|nginx)$ ]] || fail 10 "Invalid web server"
[[ "$HTTPS" =~ ^(yes|no)$ ]] || fail 10 "Invalid --https"
[[ "$DB" =~ ^(|sqlite|postgres|none)$ ]] || fail 10 "Invalid --db"
[[ "$INSTALL" =~ ^[a-z,-]*$ ]] || fail 10 "Invalid --install"
[ "$TO" = "last" ] || valid_ts "$TO" || fail 10 "Invalid backup id"

if [ "$(id -u)" -ne 0 ] && [ -z "${SD_ROOT:-}" ]; then fail 11 "Must run as root (use sudo)"; fi

APP_DIR="$APPS/${SVC:-_}"
ENV_FILE="$CONF/${SVC:-_}/${SVC:-_}.env"
BACKUP_DIR="$BACKUPS/${SVC:-_}"

# ---------------------------------------------------------------- helpers
service_exists() { [ -f "$UNITS/$1.service" ] || systemctl cat "$1" >/dev/null 2>&1; }

service_user() {
    local u g
    u="$(systemctl show "$1" -p User --value 2>/dev/null || true)"
    g="$(systemctl show "$1" -p Group --value 2>/dev/null || true)"
    if [ -z "$u" ] || ! id "$u" >/dev/null 2>&1; then
        u="$(stat -c '%U' "$APP_DIR" 2>/dev/null || echo root)"
        g="$(stat -c '%G' "$APP_DIR" 2>/dev/null || echo root)"
    fi
    [ -n "$g" ] || g="$(id -gn "$u" 2>/dev/null || echo "$u")"
    echo "$u:$g"
}

env_port() {
    [ -r "$ENV_FILE" ] || return 0
    grep -oE 'ASPNETCORE_URLS=.*127\.0\.0\.1:[0-9]+' "$ENV_FILE" 2>/dev/null | grep -oE '[0-9]+$' | head -n1
}

dotnet_bin() { command -v dotnet 2>/dev/null || { [ -x /usr/share/dotnet/dotnet ] && echo /usr/share/dotnet/dotnet; } || true; }

has_runtime() {
    local d; d="$(dotnet_bin)"
    [ -n "$d" ] || return 1
    "$d" --list-runtimes 2>/dev/null | grep -q "^Microsoft.AspNetCore.App $1\."
}

runtime_hint() {
    if command -v dnf >/dev/null 2>&1; then echo "sudo dnf install -y aspnetcore-runtime-$1.0"
    elif command -v apt-get >/dev/null 2>&1; then echo "sudo apt-get install -y aspnetcore-runtime-$1.0"
    else echo "install the ASP.NET Core $1.0 runtime"; fi
}


pkg_manager() {
    if command -v dnf >/dev/null 2>&1; then echo dnf
    elif command -v yum >/dev/null 2>&1; then echo yum
    elif command -v apt-get >/dev/null 2>&1; then echo apt
    else echo none; fi
}

pkg_install() {
    case "$(pkg_manager)" in
        dnf) dnf install -y "$@" 2>&1 ;;
        yum) yum install -y "$@" 2>&1 ;;
        apt) DEBIAN_FRONTEND=noninteractive apt-get install -y "$@" 2>&1 ;;
        *) return 1 ;;
    esac
}

free_mb() { timeout 10 df -Pm "$1" 2>/dev/null | awk 'NR==2 {print $4}'; }

size_kb() { du -sk "$1" 2>/dev/null | awk '{print $1}'; }

fix_perms() {
    local svc="$1" owner
    owner="$(service_user "$svc")"
    chown -R "$owner" "$APPS/$svc" || return 1
    chmod -R 750 "$APPS/$svc" || return 1
    if command -v restorecon >/dev/null 2>&1; then restorecon -R "$APPS/$svc" >/dev/null 2>&1 || true; fi
}

# Makes $2 identical to $1, leaving wwwroot/uploads of $2 alone.
sync_dir() {
    local src="$1" dst="$2"
    mkdir -p "$dst" || return 1
    if command -v rsync >/dev/null 2>&1; then
        rsync -a --checksum --delete --exclude 'wwwroot/uploads' "$src/" "$dst/"
        return $?
    fi
    find "$dst" -mindepth 1 -maxdepth 1 ! -name wwwroot -exec rm -rf {} + || return 1
    if [ -d "$dst/wwwroot" ]; then find "$dst/wwwroot" -mindepth 1 -maxdepth 1 ! -name uploads -exec rm -rf {} + || return 1; fi
    (cd "$src" && tar cf - --exclude=./wwwroot/uploads .) | (cd "$dst" && tar xf -)
}

# First port from 5000 that nothing listens on and no site proxies to.
suggest_port() {
    local p=5000
    while [ "$p" -lt 5200 ]; do
        port_taken "$p" || { echo "$p"; return 0; }
        p=$((p + 1))
    done
}

# Healthy = active for 5 seconds in a row without restarting, and Kestrel answers on its port (when known).
wait_healthy() {
    local svc="$1" limit="$2" t=0 stable=0 port restarts0 restarts code
    port="$(env_port)"
    restarts0="$(systemctl show "$svc" -p NRestarts --value 2>/dev/null || echo 0)"
    while [ "$t" -lt "$limit" ]; do
        sleep 1; t=$((t + 1))
        if systemctl is-active --quiet "$svc"; then
            restarts="$(systemctl show "$svc" -p NRestarts --value 2>/dev/null || echo 0)"
            if [ "${restarts:-0}" != "${restarts0:-0}" ]; then stable=0; restarts0="$restarts"; continue; fi
            stable=$((stable + 1))
            if [ "$stable" -ge 5 ]; then
                if [ -n "$port" ] && command -v curl >/dev/null 2>&1; then
                    code="$(curl -s -o /dev/null -m 3 -w '%{http_code}' "http://127.0.0.1:$port/" 2>/dev/null || true)"
                    if [ -n "$code" ] && [ "$code" != "000" ]; then info "Kestrel answers on 127.0.0.1:$port (HTTP $code)"; return 0; fi
                else
                    return 0
                fi
            fi
        else
            stable=0
        fi
    done
    return 1
}

show_journal() { journalctl -u "$1" -n 25 --no-pager 2>/dev/null | sed 's/^/    | /' || true; }

# Settings the app complained about ("'Section:Key' is not set", "Connection string 'X' is not configured") read from stdin,
# as environment variable names, comma separated.
scan_missing() {
    grep -oE "('[A-Za-z][A-Za-z0-9_]*(:[A-Za-z0-9_]+)*' (is|are) (not set|not configured|missing|required)|[Cc]onnection string '[A-Za-z0-9_.-]+' (is|was) (not configured|not found|missing))" \
        | sed -E "s/^[Cc]onnection string '([^']+)'.*/ConnectionStrings:\1/; s/^'([^']+)'.*/\1/; s/:/__/g" | sort -u | paste -sd, - || true
}

missing_settings() { journalctl -u "$1" -n 60 --no-pager 2>/dev/null | scan_missing; }

# Runs the app found in $1 for a few seconds with the real settings of the service, on a spare port, as the service user.
# 0 = it stayed up (or it cannot be tried); 1 = it stopped by itself. Output is in $CANARY_LOG.
CANARY_LOG=""
canary() {
    local src="$1" d port owner cdir line key val limit i pid code
    d="$(dotnet_bin)"; [ -n "$d" ] || return 0
    [ -f "$src/$DLL" ] || return 1
    limit="${CANARY_WAIT:-15}"
    port=5300; while port_taken "$port" && [ "$port" -lt 5400 ]; do port=$((port + 1)); done
    owner="$(service_user "$SVC")"
    cdir="$(mktemp -d "${TMPDIR:-/var/tmp}/stackdev-canary.XXXXXX")" || return 0
    CANARY_LOG="$cdir.log"
    cp -a "$src/." "$cdir/" && chown -R "$owner" "$cdir" 2>/dev/null || true
    local envargs=("ASPNETCORE_URLS=http://127.0.0.1:$port" "HOME=$cdir" "DOTNET_CLI_TELEMETRY_OPTOUT=1")
    if [ -r "$ENV_FILE" ]; then
        while IFS= read -r line || [ -n "$line" ]; do
            [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || continue
            key="${line%%=*}"; val="${line#*=}"
            [ "$key" = ASPNETCORE_URLS ] && continue
            if [[ "$val" == \"*\" && ${#val} -ge 2 ]]; then val="${val:1:${#val}-2}"; val="${val//\\\"/\"}"; val="${val//\\\\/\\}"
            elif [[ "$val" == \'*\' && ${#val} -ge 2 ]]; then val="${val:1:${#val}-2}"; fi
            envargs+=("$key=$val")
        done < "$ENV_FILE"
    fi
    ( cd "$cdir" && exec timeout -k 2 $((limit + 10)) runuser -u "${owner%%:*}" -- env -i PATH="$PATH" "${envargs[@]}" "$d" "$cdir/$DLL" ) >"$CANARY_LOG" 2>&1 &
    pid=$!
    code=0
    for ((i = 0; i < limit * 5; i++)); do
        command -p sleep 0.2
        if ! kill -0 "$pid" 2>/dev/null; then code=1; break; fi
        if command -v curl >/dev/null 2>&1; then
            val="$(curl -s -o /dev/null -m 1 -w '%{http_code}' "http://127.0.0.1:$port/" 2>/dev/null || true)"
            if [ -n "$val" ] && [ "$val" != "000" ]; then break; fi
        fi
    done
    kill "$pid" 2>/dev/null || true
    pkill -f "$cdir/$DLL" >/dev/null 2>&1 || true
    wait "$pid" 2>/dev/null || true
    rm -rf "$cdir"
    return "$code"
}

canary_report() {
    [ -f "$CANARY_LOG" ] || return 0
    tail -n 12 "$CANARY_LOG" | sed 's/^/    | /'
    local missing; missing="$(scan_missing < "$CANARY_LOG")"
    [ -z "$missing" ] || kv missing "$missing"
    rm -f "$CANARY_LOG"
}

# Backup ids (YYYYMMDD-HHMMSS) of a service, newest first.
backup_ids() { find "$BACKUPS/$1" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null | grep -E '^[0-9]{8}-[0-9]{6}$' | sort -r; }

prune_backups() {
    local svc="$1" keep="$2" n=0 d
    [ -d "$BACKUPS/$svc" ] || return 0
    for d in $(backup_ids "$svc"); do
        n=$((n + 1))
        if [ "$n" -gt "$keep" ]; then rm -rf "${BACKUPS:?}/$svc/$d"; fi
    done
}

# A backup id (YYYYMMDD-HHMMSS) that does not exist yet, even when two operations fall in the same second.
new_backup_id() {
    local now id
    now="$(date +%s)"
    id="$(date -d "@$now" +%Y%m%d-%H%M%S)"
    while [ -e "$BACKUPS/$1/$id" ]; do now=$((now + 1)); id="$(date -d "@$now" +%Y%m%d-%H%M%S)"; done
    echo "$id"
}

latest_backup() { backup_ids "$1" | head -n1; }

restore_backup() {
    local svc="$1" ts="$2" src="$BACKUPS/$1/$2/app"
    [ -d "$src" ] || return 1
    systemctl stop "$svc" >/dev/null 2>&1 || true
    sync_dir "$src" "$APPS/$svc" || return 1
    fix_perms "$svc" || return 1
    systemctl start "$svc" || return 1
    wait_healthy "$svc" "$WAIT"
}

# ---------------------------------------------------------------- preflight
cmd_preflight() {
    STEP_TOTAL=1
    [ -n "$SVC" ] || fail 10 "--svc is required"
    kv sudo ok
    kv os "$( (. "$ROOT/etc/os-release" 2>/dev/null && echo "${PRETTY_NAME:-unknown}") || echo unknown)"
    kv service_exists "$(service_exists "$SVC" && echo yes || echo no)"
    kv app_dir "$([ -d "$APP_DIR" ] && echo yes || echo no)"
    kv active "$(timeout 15 systemctl is-active "$SVC" 2>/dev/null || echo unknown)"
    kv env_file "$([ -f "$ENV_FILE" ] && echo yes || echo no)"
    kv port "$(env_port)"
    kv deployed_content "$(head -n1 "$BACKUP_DIR/current.content" 2>/dev/null || true)"
    kv postgres "$(command -v psql >/dev/null 2>&1 && echo yes || echo no)"
    kv rsync "$(command -v rsync >/dev/null 2>&1 && echo yes || echo no)"
    kv dotnet "$(dotnet_bin | head -n1)"
    kv runtimes "$( ( timeout 15 "$(dotnet_bin)" --list-runtimes 2>/dev/null | awk '$1=="Microsoft.AspNetCore.App"{print $2}' | cut -d. -f1 | sort -un | paste -sd, - ) 2>/dev/null || true)"
    kv web "$(if command -v httpd >/dev/null 2>&1 || command -v apache2 >/dev/null 2>&1; then echo apache; elif command -v nginx >/dev/null 2>&1; then echo nginx; else echo none; fi)"
    kv certbot "$(command -v certbot >/dev/null 2>&1 && echo yes || echo no)"
    kv pkg "$(pkg_manager)"
    kv suggested_port "$(suggest_port)"
    kv free_mb "$(free_mb "$([ -d "$APPS" ] && echo "$APPS" || echo "$ROOT/")")"
    kv backups "$(backup_ids "$SVC" | wc -l)"
    echo "##DONE"
}

# ---------------------------------------------------------------- https: certificate for a site that already exists
# Without an email Let's Encrypt still issues the certificate (it just cannot send expiry reminders).
issue_certificate() {
    command -v certbot >/dev/null 2>&1 || { warn "certbot is not installed"; return 2; }
    local out mail=(-m "$EMAIL")
    [ -n "$EMAIL" ] || mail=(--register-unsafely-without-email)
    if out="$(certbot "--$WEB" -d "$DOMAIN" --redirect --non-interactive --agree-tos "${mail[@]}" 2>&1)"; then
        info "Certificate issued and HTTP now redirects to HTTPS"
        return 0
    fi
    printf '%s\n' "$out" | tail -n 8
    warn "The certificate could not be issued"
    return 1
}

cmd_https() {
    STEP_TOTAL=1
    [ -n "$DOMAIN" ] || fail 10 "Missing arguments"
    step "HTTPS certificate"
    if issue_certificate; then kv https ok; else kv https pending; fi
    echo "##DONE"
}

# ---------------------------------------------------------------- prepare: install what a new API needs
# --install is a comma list of: web-apache, web-nginx, certbot, rsync, runtime (uses --runtime N and --web).
# Every item is tried even if an earlier one fails, and what failed is reported at the end. Only distro packages are used.
# PostgreSQL server on the machine, started and enabled.
install_postgres() {
    if [ "$(pkg_manager)" = apt ]; then
        pkg_install postgresql postgresql-contrib || return 1
    else
        pkg_install postgresql-server postgresql-contrib || return 1
        if [ ! -f "$ROOT/var/lib/pgsql/data/PG_VERSION" ] && command -v postgresql-setup >/dev/null 2>&1; then postgresql-setup --initdb >/dev/null 2>&1 || true; fi
    fi
    systemctl enable --now postgresql >/dev/null 2>&1 || true
    command -v psql >/dev/null 2>&1
}

install_item() {
    case "$1" in
        web-apache)
            if [ "$(pkg_manager)" = apt ]; then pkg_install apache2 || return 1; command -v a2enmod >/dev/null 2>&1 && a2enmod proxy proxy_http headers ssl rewrite >/dev/null 2>&1 || true; systemctl enable --now apache2 >/dev/null 2>&1 || true
            else pkg_install httpd mod_ssl || return 1; systemctl enable --now httpd >/dev/null 2>&1 || true; fi ;;
        web-nginx) pkg_install nginx || return 1; systemctl enable --now nginx >/dev/null 2>&1 || true ;;
        certbot) pkg_install certbot "python3-certbot-$WEB" || return 1 ;;
        rsync) pkg_install rsync || return 1 ;;
        postgres) install_postgres || return 1 ;;
        runtime)
            [ -n "$RUNTIME" ] || return 1
            pkg_install "aspnetcore-runtime-$RUNTIME.0" || return 1
            has_runtime "$RUNTIME" || return 1 ;;
        *) return 1 ;;
    esac
}

cmd_prepare() {
    local items=() item failed=()
    IFS=',' read -ra items <<< "$INSTALL"
    [ "${#items[@]}" -gt 0 ] && [ -n "${items[0]}" ] || fail 10 "Nothing to install"
    STEP_TOTAL="${#items[@]}"
    [ "$(pkg_manager)" != none ] || fail 31 "No supported package manager (dnf, yum or apt-get) was found"
    if [ "$(pkg_manager)" = apt ]; then info "Updating the package lists"; apt-get update 2>&1 | tail -n 3 || true; fi
    for item in "${items[@]}"; do
        step "Installing $item"
        if install_item "$item"; then info "$item installed"; else warn "Could not install $item"; failed+=("$item"); fi
    done
    if [ "${#failed[@]}" -gt 0 ]; then
        kv failed "$(IFS=,; echo "${failed[*]}")"
        fail 31 "Could not install: $(IFS=,; echo "${failed[*]}")"
    fi
    echo "##DONE"
}

# ---------------------------------------------------------------- db-create: own PostgreSQL user and database for one API
pg() { runuser -u postgres -- psql -v ON_ERROR_STOP=1 -qtA "$@"; }

cmd_db_create() {
    STEP_TOTAL=3
    [ -n "$SVC" ] && [ -n "$RUN_DIR" ] && [ -n "$PW_FILE" ] || fail 10 "Missing arguments"
    inside_run_dir "$PW_FILE" || fail 10 "File outside the run directory"
    [ -f "$PW_FILE" ] || fail 12 "The uploaded file is not there"
    local pw name user
    pw="$(head -n1 "$PW_FILE" | tr -d '\r\n')"
    shred -u "$PW_FILE" 2>/dev/null || rm -f "$PW_FILE"
    [[ "$pw" =~ ^[A-Za-z0-9]{16,64}$ ]] || fail 10 "Invalid database password"
    name="${SVC//-/_}"; user="${name}_app"

    step "Checking PostgreSQL"
    if ! command -v psql >/dev/null 2>&1; then
        info "PostgreSQL is not installed; installing it"
        [ "$(pkg_manager)" != none ] || fail 31 "No supported package manager (dnf, yum or apt-get) was found"
        if [ "$(pkg_manager)" = apt ]; then apt-get update 2>&1 | tail -n 1 || true; fi
        install_postgres || fail 31 "Could not install PostgreSQL"
    fi
    systemctl is-active --quiet postgresql 2>/dev/null || systemctl start postgresql >/dev/null 2>&1 || true

    step "Creating the user $user"
    local exists
    exists="$(pg -c "SELECT 1 FROM pg_roles WHERE rolname='$user'" 2>/dev/null || true)"
    if [ "$exists" = 1 ]; then
        printf "ALTER ROLE %s WITH LOGIN PASSWORD '%s';\n" "$user" "$pw" | pg >/dev/null || fail 30 "Could not set the database password"
        info "The user $user already existed; its password was renewed"
    else
        printf "CREATE ROLE %s WITH LOGIN PASSWORD '%s';\n" "$user" "$pw" | pg >/dev/null || fail 30 "Could not create the database user"
    fi

    step "Creating the database $name"
    exists="$(pg -c "SELECT 1 FROM pg_database WHERE datname='$name'" 2>/dev/null || true)"
    if [ "$exists" != 1 ]; then
        pg -c "CREATE DATABASE $name OWNER $user" >/dev/null || fail 30 "Could not create the database"
    fi
    pg -c "REVOKE ALL ON DATABASE $name FROM PUBLIC" >/dev/null 2>&1 || true
    kv db_name "$name"; kv db_user "$user"; kv db_host 127.0.0.1; kv db_port 5432
    echo "##DONE"
}

# ---------------------------------------------------------------- check: would the installed app start?
cmd_check() {
    STEP_TOTAL=1
    [ -n "$SVC" ] && [ -n "$DLL" ] || fail 10 "Missing arguments"
    service_exists "$SVC" || fail 11 "The service '$SVC' does not exist on this server. Provision it first."
    step "Trying the app with its current settings"
    [ -f "$APP_DIR/$DLL" ] || fail 24 "There is no application in $APP_DIR yet. Upload it first."
    if ! canary "$APP_DIR"; then
        warn "The application does not start with the current settings"
        canary_report
        fail 24 "The application does not start with the current settings."
    fi
    rm -f "$CANARY_LOG"
    echo "##DONE"
}

# ---------------------------------------------------------------- discover: dotnet services already installed
cmd_discover() {
    local f name dir user dll state exe
    shopt -s nullglob
    for f in "$UNITS"/*.service; do
        exe="$(grep -E '^ExecStart=' "$f" 2>/dev/null | head -n1 || true)"
        [[ "$exe" == *dotnet* ]] || continue
        name="$(basename "$f" .service)"
        dll="$(echo "$exe" | grep -oE '[A-Za-z0-9._-]+\.dll' | head -n1 || true)"
        dir="$(grep -E '^WorkingDirectory=' "$f" | head -n1 | cut -d= -f2- || true)"
        user="$(grep -E '^User=' "$f" | head -n1 | cut -d= -f2- || true)"
        state="$(systemctl is-active "$name" 2>/dev/null || echo unknown)"
        echo "##API $name|$dll|$dir|$user|$state"
    done
    echo "##DONE"
}

# ---------------------------------------------------------------- deploy
cmd_deploy() {
    STEP_TOTAL=6
    [ -n "$SVC" ] && [ -n "$DLL" ] && [ -n "$RUN_DIR" ] && [ -n "$BUNDLE" ] && [ -n "$SHA" ] || fail 10 "Missing arguments"
    inside_run_dir "$BUNDLE" || fail 10 "Bundle outside the run directory"

    step "Checking the upload and the server"
    [ -f "$BUNDLE" ] || fail 12 "The uploaded file is not there"
    if ! echo "$SHA  $BUNDLE" | sha256sum -c --status 2>/dev/null; then fail 12 "The upload is corrupt (checksum does not match). Nothing was changed."; fi
    info "Checksum OK"
    service_exists "$SVC" || fail 11 "The service '$SVC' does not exist on this server. Provision it first."
    [ -d "$APP_DIR" ] || fail 11 "$APP_DIR does not exist. Provision the API first."
    if [ -n "$RUNTIME" ] && ! has_runtime "$RUNTIME"; then
        fail 11 "ASP.NET Core runtime $RUNTIME is not installed on the server. Install it with: $(runtime_hint "$RUNTIME")"
    fi
    local first="no"
    [ -f "$APP_DIR/$DLL" ] || first="yes"
    local new="$RUN_DIR/new" need have
    rm -rf "$new"; mkdir -p "$new"
    tar -xzf "$BUNDLE" -C "$new" || fail 12 "The archive could not be extracted"
    [ -f "$new/$DLL" ] || fail 12 "$DLL is not at the root of the upload"
    need=$(( $(size_kb "$new") / 1024 * 3 + $(size_kb "$APP_DIR") / 1024 + 50 ))
    have="$(free_mb "$APPS")"
    if [ -n "$have" ] && [ "$have" -lt "$need" ]; then fail 11 "Not enough disk space (need about ${need} MB, ${have} MB free)"; fi

    info "Trying the new version with the current settings before replacing anything"
    if ! canary "$new"; then
        warn "The new version does not start with the current settings"
        canary_report
        rm -rf "$RUN_DIR/new"
        fail 24 "The new version does not start with the current settings. Nothing was changed; the current version keeps running."
    fi
    rm -f "$CANARY_LOG"

    step "Backing up the running version"
    local ts; ts="$(new_backup_id "$SVC")"
    mkdir -p "$BACKUP_DIR/$ts/app" || fail 11 "Cannot create the backup folder"
    chmod 700 "$BACKUPS" "$BACKUP_DIR" "$BACKUP_DIR/$ts" 2>/dev/null || true
    if command -v rsync >/dev/null 2>&1; then
        rsync -a --exclude 'wwwroot/uploads' "$APP_DIR/" "$BACKUP_DIR/$ts/app/" || fail 11 "Backup failed; nothing was changed"
    else
        (cd "$APP_DIR" && tar cf - --exclude=./wwwroot/uploads .) | (cd "$BACKUP_DIR/$ts/app" && tar xf -) || fail 11 "Backup failed; nothing was changed"
    fi
    echo "dll=$DLL" > "$BACKUP_DIR/$ts/info"
    info "Backup $ts saved"

    step "Stopping $SVC"
    systemctl stop "$SVC" || fail 11 "Could not stop the service; nothing was changed"

    local broken=""
    step "Replacing the files"
    if ! sync_dir "$new" "$APP_DIR"; then broken="copying the files failed"; fi

    if [ -z "$broken" ]; then
        step "Setting permissions"
        fix_perms "$SVC" || broken="setting permissions failed"
    else
        STEP_N=$((STEP_N + 1))
    fi

    if [ -z "$broken" ]; then
        step "Starting $SVC and checking it is healthy"
        systemctl start "$SVC" || broken="the service did not start"
        if [ -z "$broken" ] && ! wait_healthy "$SVC" "$WAIT"; then broken="the service is not healthy after ${WAIT}s"; fi
    else
        STEP_N=$((STEP_N + 1))
    fi

    if [ -n "$broken" ]; then
        warn "Deployment failed: $broken"
        show_journal "$SVC"
        local missing; missing="$(missing_settings "$SVC")"
        [ -z "$missing" ] || kv missing "$missing"
        if [ "$first" = "yes" ]; then
            # Nothing to go back to: keep the uploaded files, stop the crash loop and let the user fix the settings.
            systemctl stop "$SVC" >/dev/null 2>&1 || true
            rm -rf "$RUN_DIR/new"
            fail 22 "First upload: the files are on the server but the service did not start ($broken). It is stopped; there was no previous version to go back to."
        fi
        STEP_TOTAL=7
        step "Going back to the previous version ($ts)"
        if restore_backup "$SVC" "$ts"; then
            rm -rf "$RUN_DIR/new"
            fail 20 "The new version failed ($broken). The previous version is running again."
        fi
        show_journal "$SVC"
        fail 21 "The new version failed ($broken) AND the automatic rollback failed. Backup $ts is in $BACKUP_DIR/$ts/app"
    fi

    prune_backups "$SVC" "$KEEP"
    rm -rf "$RUN_DIR/new"
    if [ -n "$CONTENT" ]; then echo "$CONTENT" > "$BACKUP_DIR/current.content" 2>/dev/null || true; else rm -f "$BACKUP_DIR/current.content"; fi
    kv backup "$ts"
    kv active "$(timeout 15 systemctl is-active "$SVC" 2>/dev/null || echo unknown)"
    echo "##DONE"
}

# ---------------------------------------------------------------- rollback / backups
cmd_backups() {
    [ -n "$SVC" ] || fail 10 "--svc is required"
    local d
    for d in $(backup_ids "$SVC"); do
        echo "##BACKUP $d|$(date -d "${d:0:4}-${d:4:2}-${d:6:2} ${d:9:2}:${d:11:2}:${d:13:2}" '+%Y-%m-%d %H:%M:%S' 2>/dev/null || echo "$d")|$(size_kb "$BACKUP_DIR/$d")"
    done
    echo "##DONE"
}

cmd_rollback() {
    STEP_TOTAL=2
    [ -n "$SVC" ] || fail 10 "--svc is required"
    local ts="$TO"
    [ "$ts" = "last" ] && ts="$(latest_backup "$SVC")"
    [ -n "$ts" ] && [ -d "$BACKUP_DIR/$ts/app" ] || fail 11 "There is no backup to go back to"
    step "Saving the current version before going back"
    local safe; safe="$(new_backup_id "$SVC")"
    mkdir -p "$BACKUP_DIR/$safe/app" && sync_dir "$APP_DIR" "$BACKUP_DIR/$safe/app" || warn "Could not save the current version"
    step "Restoring $ts"
    if restore_backup "$SVC" "$ts"; then rm -f "$BACKUP_DIR/current.content"; kv restored "$ts"; echo "##DONE"; return 0; fi
    show_journal "$SVC"
    fail 21 "The restored version is not healthy either. Backups are in $BACKUP_DIR"
}

# ---------------------------------------------------------------- env
cmd_env_read() {
    [ -n "$SVC" ] || fail 10 "--svc is required"
    [ -f "$ENV_FILE" ] || fail 11 "$ENV_FILE does not exist"
    echo "##ENV-BEGIN"
    cat "$ENV_FILE"
    echo "##ENV-END"
    echo "##DONE"
}

cmd_env_write() {
    STEP_TOTAL=3
    [ -n "$SVC" ] && [ -n "$RUN_DIR" ] && [ -n "$ENV_FILE_IN" ] || fail 10 "Missing arguments"
    inside_run_dir "$ENV_FILE_IN" || fail 10 "File outside the run directory"
    [ -f "$ENV_FILE_IN" ] || fail 12 "The uploaded file is not there"
    [ -f "$ENV_FILE" ] || fail 11 "$ENV_FILE does not exist; provision the API first"

    step "Checking the new file"
    local bad
    bad="$(grep -vnE '^[[:space:]]*(#.*)?$|^[A-Za-z_][A-Za-z0-9_]*=' "$ENV_FILE_IN" | head -n1 || true)"
    [ -z "$bad" ] || fail 40 "Line not valid in an environment file: ${bad%%:*}"

    step "Backing up the current file"
    local ts; ts="$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$BACKUP_DIR" && chmod 700 "$BACKUPS" "$BACKUP_DIR" 2>/dev/null || true
    cp -p "$ENV_FILE" "$BACKUP_DIR/env-$ts" && chmod 600 "$BACKUP_DIR/env-$ts" || fail 40 "Cannot back up the current file"

    step "Installing the new file"
    local owner group tmp
    owner="$(stat -c '%U' "$ENV_FILE")"; group="$(stat -c '%G' "$ENV_FILE")"
    tmp="$ENV_FILE.new.$$"
    cp "$ENV_FILE_IN" "$tmp" && chown "$owner:$group" "$tmp" && chmod 640 "$tmp" && mv -f "$tmp" "$ENV_FILE" || { rm -f "$tmp"; fail 40 "Could not install the file"; }
    if command -v restorecon >/dev/null 2>&1; then restorecon "$ENV_FILE" >/dev/null 2>&1 || true; fi
    shred -u "$ENV_FILE_IN" 2>/dev/null || rm -f "$ENV_FILE_IN"
    kv env_backup "env-$ts"

    if [ "$RESTART" = "yes" ]; then
        STEP_TOTAL=4
        local was_up="no"
        systemctl is-active --quiet "$SVC" && was_up="yes"
        step "Restarting $SVC to apply it"
        systemctl restart "$SVC" || true
        if ! wait_healthy "$SVC" "$WAIT"; then
            warn "The service is not healthy with the new values"
            show_journal "$SVC"
            local missing; missing="$(missing_settings "$SVC")"
            [ -z "$missing" ] || kv missing "$missing"
            if [ "$was_up" = "no" ]; then
                # it was already down before this change: the new file is not what broke it, keep it
                systemctl stop "$SVC" >/dev/null 2>&1 || true
                fail 23 "The values were saved, but the service still does not start."
            fi
            cp -p "$BACKUP_DIR/env-$ts" "$ENV_FILE" && systemctl restart "$SVC" || true
            if wait_healthy "$SVC" "$WAIT"; then fail 20 "The new values broke the service; the previous file is back and the service runs again."; fi
            fail 21 "The new values broke the service and it is still down with the previous file. Backup: $BACKUP_DIR/env-$ts"
        fi
    fi
    echo "##DONE"
}

# ---------------------------------------------------------------- provision
render() {
    sed -e "s|__SVC__|$SVC|g" -e "s|__DLL__|$DLL|g" -e "s|__DOMAIN__|$DOMAIN|g" -e "s|__PORT__|$PORT|g" \
        -e "s|__APP_DIR__|${APP_DIR#"$ROOT"}|g" -e "s|__DATA_DIR__|${DATA#"$ROOT"}/$SVC|g" -e "s|__ENV_FILE__|${ENV_FILE#"$ROOT"}|g" \
        -e "s|__DOTNET__|$(dotnet_bin)|g" -e "s|__CONN__|conn_$(echo "$SVC" | tr '-' '_')|g"
}

port_taken() {
    if command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$1\$"; then return 0; fi
    grep -rqsE "127\.0\.0\.1:$1([^0-9]|\$)" "$ROOT/etc/httpd/conf.d" "$ROOT/etc/apache2/sites-available" "$ROOT/etc/nginx/conf.d" 2>/dev/null
}

apache_vhost_path() {
    if [ -d "$ROOT/etc/apache2/sites-available" ]; then echo "$ROOT/etc/apache2/sites-available/$SVC.conf"; else echo "$ROOT/etc/httpd/conf.d/$SVC.conf"; fi
}

cmd_provision() {
    STEP_TOTAL=8
    [ -n "$SVC" ] && [ -n "$DLL" ] && [ -n "$DOMAIN" ] && [ -n "$PORT" ] && [ -n "$RUN_DIR" ] && [ -n "$ENV_FILE_IN" ] || fail 10 "Missing arguments"
    inside_run_dir "$ENV_FILE_IN" || fail 10 "File outside the run directory"
    [ -f "$ENV_FILE_IN" ] || fail 12 "The uploaded env file is not there"

    local unit="$UNITS/$SVC.service" vhost
    if [ "$WEB" = "apache" ]; then vhost="$(apache_vhost_path)"; else vhost="$ROOT/etc/nginx/conf.d/$SVC.conf"; fi

    step "Checking the server"
    if service_exists "$SVC" && [ "$FORCE" != "yes" ]; then fail 11 "The service '$SVC' already exists. Deploy to it, or provision again with overwrite."; fi
    port_taken "$PORT" && [ "$FORCE" != "yes" ] && fail 11 "Port $PORT is already in use or assigned to another site"
    if [ "$WEB" = "apache" ] && ! command -v httpd >/dev/null 2>&1 && ! command -v apache2 >/dev/null 2>&1; then fail 11 "Apache is not installed"; fi
    if [ "$WEB" = "nginx" ] && ! command -v nginx >/dev/null 2>&1; then fail 11 "Nginx is not installed"; fi
    if [ -n "$RUNTIME" ] && ! has_runtime "$RUNTIME"; then
        warn "ASP.NET Core runtime $RUNTIME is not installed. Install it before the first deploy: $(runtime_hint "$RUNTIME")"
    fi
    [ -z "$(dotnet_bin)" ] && warn "dotnet was not found on the server"
    if [ -f "$vhost" ] && [ "$FORCE" != "yes" ]; then fail 11 "$vhost already exists (another site?)"; fi

    step "Creating the service user and folders"
    if ! id "$SVC" >/dev/null 2>&1; then useradd --system --no-create-home --shell /sbin/nologin "$SVC" || fail 30 "Could not create the user $SVC"; fi
    mkdir -p "$APP_DIR" "$DATA/$SVC/uploads" "$CONF/$SVC" || fail 30 "Could not create the folders"
    chown -R "$SVC:$SVC" "$DATA/$SVC" && chmod -R 750 "$DATA/$SVC" || fail 30 "Could not set permissions on the data folder"
    chown "$SVC:$SVC" "$APP_DIR" && chmod 750 "$APP_DIR" || true

    step "Installing the secrets file"
    if [ -f "$ENV_FILE" ]; then
        mkdir -p "$BACKUP_DIR" && chmod 700 "$BACKUPS" "$BACKUP_DIR" 2>/dev/null || true
        cp -p "$ENV_FILE" "$BACKUP_DIR/env-$(date +%Y%m%d-%H%M%S)" || true
    fi
    cp "$ENV_FILE_IN" "$ENV_FILE" && chown "root:$SVC" "$ENV_FILE" && chmod 640 "$ENV_FILE" || fail 30 "Could not install the env file"
    shred -u "$ENV_FILE_IN" 2>/dev/null || rm -f "$ENV_FILE_IN"
    info "Secrets installed in ${ENV_FILE#"$ROOT"} (readable only by root and $SVC)"

    step "Creating the systemd service"
    render > "$unit" <<'UNIT_EOF' || fail 30 "Could not write the unit"
[Unit]
Description=__SVC__ (.NET API)
After=network.target

[Service]
Type=exec
User=__SVC__
Group=__SVC__
WorkingDirectory=__APP_DIR__
ExecStart=__DOTNET__ __APP_DIR__/__DLL__
EnvironmentFile=__ENV_FILE__
Environment=HOME=__DATA_DIR__
Restart=always
RestartSec=5
SyslogIdentifier=__SVC__
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=__DATA_DIR__
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true

[Install]
WantedBy=multi-user.target
UNIT_EOF
    systemctl daemon-reload || fail 30 "daemon-reload failed"
    systemctl enable "$SVC" >/dev/null 2>&1 || fail 30 "Could not enable the service"
    info "Service enabled (it starts with the first deployment)"

    step "SELinux and firewall"
    if command -v getenforce >/dev/null 2>&1 && [ "$(getenforce 2>/dev/null)" != "Disabled" ]; then
        if command -v setsebool >/dev/null 2>&1; then
            setsebool -P httpd_can_network_connect 1 >/dev/null 2>&1 && info "SELinux: the web server may reach the API" || warn "Could not set httpd_can_network_connect"
        fi
    fi
    if command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active --quiet firewalld 2>/dev/null; then
        firewall-cmd --permanent --add-service=http >/dev/null 2>&1; firewall-cmd --permanent --add-service=https >/dev/null 2>&1
        firewall-cmd --reload >/dev/null 2>&1 && info "firewalld: http and https allowed" || warn "Could not reload firewalld"
    elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
        ufw allow 80/tcp >/dev/null 2>&1; ufw allow 443/tcp >/dev/null 2>&1; info "ufw: ports 80 and 443 allowed"
    else
        info "No local firewall detected"
    fi
    warn "In your cloud (Oracle, AWS...) the network rules must also allow ports 80 and 443."

    step "Configuring the web server ($WEB)"
    if [ -f "$vhost" ]; then cp -p "$vhost" "$vhost.stackdev-$(date +%Y%m%d-%H%M%S).bak"; fi
    if [ "$WEB" = "apache" ]; then
        render > "$vhost" <<'APACHE_EOF' || fail 30 "Could not write the Apache site"
<VirtualHost *:80>
    ServerName __DOMAIN__

    ProxyRequests Off
    ProxyPreserveHost On
    ProxyPass /.well-known/acme-challenge !
    ProxyPass / http://127.0.0.1:__PORT__/
    ProxyPassReverse / http://127.0.0.1:__PORT__/
    RequestHeader set X-Forwarded-Proto "http"

    ErrorLog  logs/__SVC__-error.log
    CustomLog logs/__SVC__-access.log combined
</VirtualHost>
APACHE_EOF
        if command -v a2enmod >/dev/null 2>&1; then
            a2enmod proxy proxy_http headers >/dev/null 2>&1 || true
            a2ensite "$SVC" >/dev/null 2>&1 || true
        fi
        if command -v apachectl >/dev/null 2>&1; then apachectl -t >/dev/null 2>&1 || { rm -f "$vhost"; fail 30 "Apache rejected the configuration; the new site was removed"; }
        elif command -v httpd >/dev/null 2>&1; then httpd -t >/dev/null 2>&1 || { rm -f "$vhost"; fail 30 "Apache rejected the configuration; the new site was removed"; }; fi
        systemctl reload httpd >/dev/null 2>&1 || systemctl reload apache2 >/dev/null 2>&1 || fail 30 "Could not reload Apache"
    else
        render > "$vhost" <<'NGINX_EOF' || fail 30 "Could not write the Nginx site"
map $http_upgrade $__CONN__ {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name __DOMAIN__;
    client_max_body_size 25m;

    location / {
        proxy_pass http://127.0.0.1:__PORT__;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $__CONN__;
    }
}
NGINX_EOF
        nginx -t >/dev/null 2>&1 || { rm -f "$vhost"; fail 30 "Nginx rejected the configuration; the new site was removed"; }
        systemctl reload nginx >/dev/null 2>&1 || fail 30 "Could not reload Nginx"
    fi
    info "Site for $DOMAIN written to ${vhost#"$ROOT"}"

    step "HTTPS certificate"
    if [ "$HTTPS" != "yes" ]; then
        info "HTTPS skipped"
    elif issue_certificate; then
        kv https ok
    else
        kv https pending
    fi

    step "Done"
    kv provisioned "$SVC"
    echo "##DONE"
}

case "$CMD" in
    preflight)  cmd_preflight ;;
    discover)   cmd_discover ;;
    prepare)    cmd_prepare ;;
    https)      cmd_https ;;
    deploy)     cmd_deploy ;;
    rollback)   cmd_rollback ;;
    backups)    cmd_backups ;;
    env-read)   cmd_env_read ;;
    env-write)  cmd_env_write ;;
    provision)  cmd_provision ;;
    db-create)  cmd_db_create ;;
    check)      cmd_check ;;
    *) fail 10 "Unknown command: ${CMD:-<none>}" ;;
esac
