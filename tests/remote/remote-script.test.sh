#!/usr/bin/env bash
# Exercises stackdev-remote.sh in a scratch root with fake system commands. Run on Linux, WSL or Git Bash: bash tests/remote/remote-script.test.sh
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="$HERE/../../src/StackDev.Core/Deploy/Remote/stackdev-remote.sh"
T="$(mktemp -d)"
export SD_ROOT="$T/root"
STUBS="$T/stubs"; mkdir -p "$STUBS" "$SD_ROOT/var/www" "$SD_ROOT/etc/systemd/system" "$SD_ROOT/var/lib" "$SD_ROOT/etc"
PASS=0; FAIL=0
ok()  { PASS=$((PASS+1)); }
bad() { FAIL=$((FAIL+1)); echo "FAIL: $*"; }
eq()  { [ "$1" = "$2" ] && ok || bad "$3 (expected '$2', got '$1')"; }
has() { grep -q -- "$2" <<<"$1" && ok || bad "$3 (missing '$2')"; }
hasnt(){ grep -q -- "$2" <<<"$1" && bad "$3 (found '$2')" || ok; }

# ---- fake commands
cat > "$STUBS/systemctl" <<'S'
#!/usr/bin/env bash
ST="$SD_ROOT/.state"; mkdir -p "$ST"
cmd="$1"; shift
case "$cmd" in
  stop) echo inactive > "$ST/$1.state" ;;
  start|restart)
    svc="$1"
    if [ -e "$SD_ROOT/var/www/$svc/BROKEN" ] || grep -qs '^BREAK=1' "$SD_ROOT/etc/$svc/$svc.env"; then echo failed > "$ST/$svc.state"; else echo active > "$ST/$svc.state"; fi ;;
  is-active) q=0; [ "$1" = "--quiet" ] && { q=1; shift; }; s="$(cat "$ST/$1.state" 2>/dev/null || echo inactive)"; [ $q = 0 ] && echo "$s"; [ "$s" = active ]; exit $? ;;
  show) svc="$1"; shift; case "$1" in -p) p="$2";; esac
        case "$p" in User) echo "$svc";; Group) echo "$svc";; NRestarts) echo 0;; *) echo "";; esac ;;
  cat) [ -f "$SD_ROOT/etc/systemd/system/$1.service" ]; exit $? ;;
  reload|daemon-reload|enable) ;;
esac
exit 0
S
cat > "$STUBS/curl" <<'S'
#!/usr/bin/env bash
if ls "$SD_ROOT"/var/www/*/BROKEN >/dev/null 2>&1; then echo 000; else echo 200; fi
S
# apachectl and a2dissite are stubbed too: a real one on the host would test the host's configuration, not the sandbox
for n in chown restorecon useradd setsebool firewall-cmd a2enmod a2ensite a2dissite apachectl httpd nginx sleep; do printf '#!/bin/sh\nexit 0\n' > "$STUBS/$n"; done
cat > "$STUBS/journalctl" <<'S'
#!/bin/sh
echo "line 1 of the journal"
[ -f "$SD_ROOT/.journal" ] && cat "$SD_ROOT/.journal"
exit 0
S
printf '#!/bin/sh\necho Enforcing\n' > "$STUBS/getenforce"
cat > "$STUBS/id" <<'S'
#!/bin/sh
case "$1" in -u) /usr/bin/id -u ;; -gn) echo "$2" ;; *) exit 0 ;; esac
S
printf '#!/bin/sh\necho "/usr/bin/dotnet"\n' > "$STUBS/dotnet_unused"
cat > "$STUBS/dotnet" <<'S'
#!/bin/sh
case "$1" in
  --list-runtimes) echo "Microsoft.AspNetCore.App 9.0.1 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]"; exit 0 ;;
  *.dll)
    if [ -e "$(dirname "$1")/NOSTART" ]; then
      echo "Unhandled exception. System.InvalidOperationException: Insecure configuration: 'Identity:Jwt:SigningKey' is not set; 'Payments:WebhookSecret' is not set." >&2
      exit 134
    fi
    exec /bin/sleep 30 ;;
esac
exit 0
S
printf '#!/bin/sh\nshift 3\nexec "$@"\n' > "$STUBS/runuser"
cat > "$STUBS/psql" <<'S'
#!/bin/sh
echo "ARGS: $*" >> "$SD_ROOT/.psql.log"
[ -t 0 ] || cat >> "$SD_ROOT/.psql.log"
exit 0
S
chmod +x "$STUBS"/*
export PATH="$STUBS:$PATH"

R() { bash "$SCRIPT" "$@" 2>&1; echo "EXIT=$?"; }
code() { grep -o 'EXIT=[0-9]*' <<<"$1" | cut -d= -f2; }

svc=demo-api
mkapp() { mkdir -p "$SD_ROOT/var/www/$svc/wwwroot/uploads" "$SD_ROOT/etc/$svc"; echo v1 > "$SD_ROOT/var/www/$svc/Demo.Api.dll"; echo keep > "$SD_ROOT/var/www/$svc/wwwroot/uploads/a.png"; echo old > "$SD_ROOT/var/www/$svc/old.txt"
  echo "[Service]" > "$SD_ROOT/etc/systemd/system/$svc.service"; echo "ASPNETCORE_URLS=http://127.0.0.1:5001" > "$SD_ROOT/etc/$svc/$svc.env"; echo active > "$SD_ROOT/.state/$svc.state" 2>/dev/null || { mkdir -p "$SD_ROOT/.state"; echo active > "$SD_ROOT/.state/$svc.state"; }; }
mkbundle() { # $1 name, rest: files
  local d="$T/b-$1"; rm -rf "$d"; mkdir -p "$d"; shift 2>/dev/null; echo "$1" > "$d/Demo.Api.dll"; shift; for f in "$@"; do touch "$d/$f"; done
  tar -czf "$T/$(basename "$d").tgz" -C "$d" . ; echo "$T/$(basename "$d").tgz"; }
RUN="$T/run"; mkdir -p "$RUN"
sha() { sha256sum "$1" | cut -d' ' -f1; }
mkapp

echo "== preflight / discover"
o="$(R preflight --svc $svc)"; has "$o" "##KV service_exists=yes" "preflight service"; has "$o" "##KV runtimes=9" "preflight runtimes"; has "$o" "##KV port=5001" "preflight port"; eq "$(code "$o")" 0 "preflight exit"
o="$(R discover)"; eq "$(code "$o")" 0 "discover exit"
mkdir -p "$SD_ROOT/etc/nginx/conf.d"; printf "server {
    server_name _ api.midominio.com www.midominio.com;
    location / { proxy_pass http://127.0.0.1:5001; }
}
" > "$SD_ROOT/etc/nginx/conf.d/other-name.conf"
o="$(R preflight --svc $svc)"; has "$o" "##KV domain=api.midominio.com" "domain found by the port it proxies"
printf "[Service]
ExecStart=/usr/bin/dotnet /var/www/$svc/Demo.Api.dll
User=x
WorkingDirectory=/var/www/$svc
" > "$SD_ROOT/etc/systemd/system/$svc.service"
o="$(R discover)"; has "$o" "|api.midominio.com" "discover lists the domain"
rm -f "$SD_ROOT/etc/nginx/conf.d/other-name.conf"
o="$(R preflight --svc $svc)"; has "$o" "##KV domain=" "no site, empty domain"

echo "== deploy ok"
mkdir -p "$T/b1" && echo v2 > "$T/b1/Demo.Api.dll" && echo n > "$T/b1/new.txt" && tar -czf "$RUN/bundle.tgz" -C "$T/b1" .
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/bundle.tgz" --sha256 "$(sha "$RUN/bundle.tgz")" --runtime 9 --wait 12)"
eq "$(code "$o")" 0 "deploy exit"; has "$o" "##DONE" "deploy done"; has "$o" "##STEP 6/6" "step 6"
eq "$(cat "$SD_ROOT/var/www/$svc/Demo.Api.dll")" v2 "new dll"
[ ! -e "$SD_ROOT/var/www/$svc/old.txt" ] && ok || bad "old file should be removed"
eq "$(cat "$SD_ROOT/var/www/$svc/wwwroot/uploads/a.png")" keep "uploads kept"
eq "$(find "$SD_ROOT/var/backups/stackdev/$svc" -mindepth 1 -maxdepth 1 -type d | wc -l)" 1 "one backup"
eq "$(cat "$SD_ROOT/.state/$svc.state")" active "service active"

echo "== deploy: wrong checksum changes nothing"
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/bundle.tgz" --sha256 0000000000000000000000000000000000000000000000000000000000000000 --wait 12)"
eq "$(code "$o")" 12 "bad sha exit"; eq "$(cat "$SD_ROOT/var/www/$svc/Demo.Api.dll")" v2 "unchanged after bad sha"

echo "== deploy: dll missing from upload"
mkdir -p "$T/b2" && echo x > "$T/b2/Other.dll" && tar -czf "$RUN/b2.tgz" -C "$T/b2" .
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/b2.tgz" --sha256 "$(sha "$RUN/b2.tgz")" --wait 12)"
eq "$(code "$o")" 12 "missing dll exit"; eq "$(cat "$SD_ROOT/var/www/$svc/Demo.Api.dll")" v2 "unchanged after missing dll"
eq "$(cat "$SD_ROOT/.state/$svc.state")" active "service untouched by a failed upload check"

echo "== deploy: broken version rolls back"
systemctl start $svc
mkdir -p "$T/b3" && echo v3 > "$T/b3/Demo.Api.dll" && touch "$T/b3/BROKEN" && tar -czf "$RUN/b3.tgz" -C "$T/b3" .
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/b3.tgz" --sha256 "$(sha "$RUN/b3.tgz")" --wait 8)"
eq "$(code "$o")" 20 "rollback exit"; has "$o" "##FAIL 20" "fail line"; has "$o" "##STEP 7/7" "rollback step"
eq "$(cat "$SD_ROOT/var/www/$svc/Demo.Api.dll")" v2 "previous version restored"
[ ! -e "$SD_ROOT/var/www/$svc/BROKEN" ] && ok || bad "broken marker should be gone"
eq "$(cat "$SD_ROOT/.state/$svc.state")" active "service active after rollback"
eq "$(cat "$SD_ROOT/var/www/$svc/wwwroot/uploads/a.png")" keep "uploads kept after rollback"

echo "== manual rollback and backups listing"
o="$(R backups --svc $svc)"; has "$o" "##BACKUP " "backups listed"
echo v-extra > "$SD_ROOT/var/www/$svc/Demo.Api.dll"
o="$(R rollback --svc $svc --wait 8)"; eq "$(code "$o")" 0 "manual rollback exit"; has "$o" "##KV restored=" "restored kv"

echo "== first upload that does not start keeps the files, stops the service and names the missing settings"
fs=first-api
mkdir -p "$SD_ROOT/var/www/$fs" "$SD_ROOT/etc/$fs"; echo "[Service]" > "$SD_ROOT/etc/systemd/system/$fs.service"
echo "ASPNETCORE_URLS=http://127.0.0.1:5002" > "$SD_ROOT/etc/$fs/$fs.env"; echo inactive > "$SD_ROOT/.state/$fs.state"
cat > "$SD_ROOT/.journal" <<'J'
System.InvalidOperationException: Insecure configuration: 'Identity:Jwt:SigningKey' is not set; 'Payments:WebhookSecret' is not set.
J
mkdir -p "$T/b4" && echo f1 > "$T/b4/First.Api.dll" && touch "$T/b4/BROKEN" && tar -czf "$RUN/b4.tgz" -C "$T/b4" .
o="$(R deploy --svc $fs --dll First.Api.dll --run-dir "$RUN" --bundle "$RUN/b4.tgz" --sha256 "$(sha "$RUN/b4.tgz")" --wait 6)"
eq "$(code "$o")" 22 "first deploy exit"; has "$o" "##FAIL 22" "first deploy fail line"
has "$o" "##KV missing=Identity__Jwt__SigningKey,Payments__WebhookSecret" "missing settings listed"
eq "$(cat "$SD_ROOT/var/www/$fs/First.Api.dll")" f1 "uploaded files kept on a first deploy"
eq "$(cat "$SD_ROOT/.state/$fs.state")" inactive "service stopped, no crash loop"
rm -f "$SD_ROOT/.journal" "$SD_ROOT/var/www/$fs/BROKEN"

echo "== db-create makes a user and a database of its own"
printf 'Ab3dEf6hIj9lMn2pQr5tUv8x\n' > "$RUN/db.pw"
o="$(R db-create --svc commerce-hub-api --run-dir "$RUN" --pw-file "$RUN/db.pw")"
eq "$(code "$o")" 0 "db-create exit"; has "$o" "##KV db_name=commerce_hub_api" "db name"; has "$o" "##KV db_user=commerce_hub_api_app" "db user"
has "$(cat "$SD_ROOT/.psql.log")" "CREATE ROLE commerce_hub_api_app WITH LOGIN PASSWORD 'Ab3dEf6hIj9lMn2pQr5tUv8x'" "role created from stdin"
has "$(cat "$SD_ROOT/.psql.log")" "CREATE DATABASE commerce_hub_api OWNER commerce_hub_api_app" "database created"
hasnt "$o" "Ab3dEf6hIj9lMn2pQr5tUv8x" "password never printed"
[ ! -e "$RUN/db.pw" ] && ok || bad "password file should be removed"
printf "bad'; DROP\n" > "$RUN/db2.pw"
o="$(R db-create --svc commerce-hub-api --run-dir "$RUN" --pw-file "$RUN/db2.pw")"; eq "$(code "$o")" 10 "unsafe password rejected"
o="$(R db-create --svc commerce-hub-api --run-dir "$RUN" --pw-file /etc/passwd)"; eq "$(code "$o")" 10 "password file outside run dir"
rm -f "$SD_ROOT/.psql.log"

echo "== the deployed content is remembered so the same build is not uploaded again"
h1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
mkdir -p "$T/b6" && echo v6 > "$T/b6/Demo.Api.dll" && tar -czf "$RUN/b6.tgz" -C "$T/b6" .
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/b6.tgz" --sha256 "$(sha "$RUN/b6.tgz")" --content $h1 --wait 12)"; eq "$(code "$o")" 0 "deploy with content exit"
o="$(R preflight --svc $svc)"; has "$o" "##KV deployed_content=$h1" "preflight reports the deployed content"
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/b6.tgz" --sha256 "$(sha "$RUN/b6.tgz")" --content notahash --wait 12)"; eq "$(code "$o")" 10 "bad content hash rejected"
o="$(R rollback --svc $svc --wait 8)"; eq "$(code "$o")" 0 "rollback after content deploy"
o="$(R preflight --svc $svc)"; hasnt "$o" "deployed_content=$h1" "rollback forgets the content"

echo "== a new version that cannot start is caught before anything is replaced"
mkdir -p "$T/b5" && echo v9 > "$T/b5/Demo.Api.dll" && touch "$T/b5/NOSTART" && tar -czf "$RUN/b5.tgz" -C "$T/b5" .
before="$(find "$SD_ROOT/var/backups/stackdev/$svc" -mindepth 1 -maxdepth 1 -type d | wc -l)"
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/b5.tgz" --sha256 "$(sha "$RUN/b5.tgz")" --wait 8)"
eq "$(code "$o")" 24 "canary exit"; has "$o" "##FAIL 24" "canary fail line"
has "$o" "##KV missing=Identity__Jwt__SigningKey,Payments__WebhookSecret" "canary names the missing settings"
eq "$(cat "$SD_ROOT/var/www/$svc/Demo.Api.dll")" v2 "current version untouched by the canary"
eq "$(cat "$SD_ROOT/.state/$svc.state")" active "service never stopped"
eq "$(find "$SD_ROOT/var/backups/stackdev/$svc" -mindepth 1 -maxdepth 1 -type d | wc -l)" "$before" "no backup made when the canary fails"

echo "== check: would the installed app start?"
o="$(R check --svc $svc --dll Demo.Api.dll)"; eq "$(code "$o")" 0 "check ok exit"; has "$o" "##DONE" "check done"
touch "$SD_ROOT/var/www/$svc/NOSTART"
o="$(R check --svc $svc --dll Demo.Api.dll)"; eq "$(code "$o")" 24 "check fail exit"; has "$o" "##KV missing=" "check names the missing settings"
rm -f "$SD_ROOT/var/www/$svc/NOSTART"

echo "== prune keeps N"
for i in 1 2 3 4; do /bin/sleep 1.1; echo "v$i" > "$T/b1/Demo.Api.dll"; tar -czf "$RUN/p.tgz" -C "$T/b1" .
  R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/p.tgz" --sha256 "$(sha "$RUN/p.tgz")" --keep 2 --wait 12 >/dev/null; done
n="$(find "$SD_ROOT/var/backups/stackdev/$svc" -mindepth 1 -maxdepth 1 -type d | wc -l)"; eq "$n" 2 "only 2 backups kept"

echo "== input validation (injection)"
for a in "--svc a;rm" "--svc ../x" "--svc UPPER" "--dll x.exe" "--port 80" "--domain a_b.com" "--sha256 zz" "--keep x"; do
  o="$(R preflight $a)"; eq "$(code "$o")" 10 "reject: $a"; done
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle /etc/passwd --sha256 "$(sha /etc/passwd)")"; eq "$(code "$o")" 10 "bundle outside run dir"
o="$(R deploy --svc $svc --dll Demo.Api.dll --run-dir "$RUN" --bundle "$RUN/../x.tgz" --sha256 "$(sha /etc/passwd)")"; eq "$(code "$o")" 10 "bundle with .."
o="$(R nonsense)"; eq "$(code "$o")" 10 "unknown command"

echo "== env read / write"
o="$(R env-read --svc $svc)"; has "$o" "##ENV-BEGIN" "env begin"; has "$o" "ASPNETCORE_URLS" "env content"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5001\nJwt__Key=newsecret\n# c\n' > "$RUN/new.env"
o="$(R env-write --svc $svc --run-dir "$RUN" --env-file "$RUN/new.env")"; eq "$(code "$o")" 0 "env write exit"; has "$(cat "$SD_ROOT/etc/$svc/$svc.env")" "Jwt__Key=newsecret" "env installed"
[ ! -e "$RUN/new.env" ] && ok || bad "uploaded env should be shredded"
ls "$SD_ROOT/var/backups/stackdev/$svc"/env-* >/dev/null 2>&1 && ok || bad "env backup missing"
printf 'THIS IS NOT VALID\n' > "$RUN/bad.env"
o="$(R env-write --svc $svc --run-dir "$RUN" --env-file "$RUN/bad.env")"; eq "$(code "$o")" 40 "invalid env rejected"; has "$(cat "$SD_ROOT/etc/$svc/$svc.env")" "Jwt__Key=newsecret" "env untouched"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5001\nBREAK=1\n' > "$RUN/break.env"
o="$(R env-write --svc $svc --run-dir "$RUN" --env-file "$RUN/break.env" --restart --wait 8)"
[ -n "${DEBUG:-}" ] && echo "$o"; eq "$(code "$o")" 20 "env that breaks the service rolls back"; hasnt "$(cat "$SD_ROOT/etc/$svc/$svc.env")" "BREAK=1" "previous env restored"
eq "$(cat "$SD_ROOT/.state/$svc.state")" active "service active after env rollback"

echo "== env write when the service was already down keeps the file and names what is missing"
echo inactive > "$SD_ROOT/.state/$svc.state"
echo "Unhandled exception. System.InvalidOperationException: Connection string 'Postgres' is not configured." > "$SD_ROOT/.journal"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5001\nBREAK=1\nKept__Value=yes\n' > "$RUN/down.env"
o="$(R env-write --svc $svc --run-dir "$RUN" --env-file "$RUN/down.env" --restart --wait 8)"
eq "$(code "$o")" 23 "already-down env write exit"; has "$o" "##KV missing=ConnectionStrings__Postgres" "connection string named"
has "$(cat "$SD_ROOT/etc/$svc/$svc.env")" "Kept__Value=yes" "new file kept"
eq "$(cat "$SD_ROOT/.state/$svc.state")" inactive "service stopped"
rm -f "$SD_ROOT/.journal"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5001\n' > "$SD_ROOT/etc/$svc/$svc.env"; echo active > "$SD_ROOT/.state/$svc.state"

echo "== provision (apache and nginx)"; DEBUGP=1
mkdir -p "$SD_ROOT/etc/httpd/conf.d" "$SD_ROOT/etc/nginx/conf.d"
printf 'ASPNETCORE_ENVIRONMENT=Production\nASPNETCORE_URLS=http://127.0.0.1:5050\nJwt__Key=abc\n' > "$RUN/p.env"
o="$(R provision --svc shop-api --dll Shop.Api.dll --domain shop.example.com --port 5050 --web apache --run-dir "$RUN" --env-file "$RUN/p.env" --runtime 9)"; { [ -n "${DEBUG:-}" ] || [ "$(code "$o")" != 0 ]; } && echo "$o"
eq "$(code "$o")" 0 "provision apache exit"; has "$o" "##STEP 8/8" "provision steps"
has "$(cat "$SD_ROOT/etc/systemd/system/shop-api.service")" "ExecStart=.* /var/www/shop-api/Shop.Api.dll" "unit ExecStart"
has "$(cat "$SD_ROOT/etc/systemd/system/shop-api.service")" "EnvironmentFile=/etc/shop-api/shop-api.env" "unit EnvironmentFile"
has "$(cat "$SD_ROOT/etc/httpd/conf.d/shop-api.conf")" "ServerName shop.example.com" "apache vhost"
has "$(cat "$SD_ROOT/etc/httpd/conf.d/shop-api.conf")" "127.0.0.1:5050" "apache port"
has "$(cat "$SD_ROOT/etc/shop-api/shop-api.env")" "Jwt__Key=abc" "env installed by provision"
eq "$(stat -c %a "$SD_ROOT/etc/shop-api/shop-api.env")" 640 "env mode"
[ ! -e "$RUN/p.env" ] && ok || bad "provision env should be shredded"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5051\n' > "$RUN/p.env"
o="$(R provision --svc shop-api --dll Shop.Api.dll --domain shop.example.com --port 5051 --web apache --run-dir "$RUN" --env-file "$RUN/p.env")"; eq "$(code "$o")" 11 "re-provision refused without --force"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5060\n' > "$RUN/p2.env"
o="$(R provision --svc blog-api --dll Blog.Api.dll --domain blog.example.com --port 5060 --web nginx --run-dir "$RUN" --env-file "$RUN/p2.env")"
eq "$(code "$o")" 0 "provision nginx exit"; has "$(cat "$SD_ROOT/etc/nginx/conf.d/blog-api.conf")" "proxy_pass http://127.0.0.1:5060" "nginx proxy"
has "$(cat "$SD_ROOT/etc/nginx/conf.d/blog-api.conf")" "map \$http_upgrade \$conn_blog_api" "nginx unique map"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5050\n' > "$RUN/p3.env"
o="$(R provision --svc other-api --dll O.dll --domain other.example.com --port 5050 --web apache --run-dir "$RUN" --env-file "$RUN/p3.env")"; eq "$(code "$o")" 11 "port conflict refused"

echo "== prepare (install what is missing)"
printf '#!/bin/sh\necho "dnf $*"\necho "$*" >> "$SD_ROOT/.pkgs"\nexit 0\n' > "$STUBS/dnf"; chmod +x "$STUBS/dnf"
o="$(R preflight --svc $svc)"; has "$o" "##KV pkg=dnf" "preflight reports the package manager"
rm -f "$SD_ROOT/.pkgs"
o="$(R prepare --install web-apache,rsync,certbot --web apache)"
eq "$(code "$o")" 0 "prepare exit"; has "$o" "##STEP 3/3" "prepare steps"; has "$o" "##DONE" "prepare done"
has "$(cat "$SD_ROOT/.pkgs")" "httpd mod_ssl" "apache packages"; has "$(cat "$SD_ROOT/.pkgs")" "rsync" "rsync package"; has "$(cat "$SD_ROOT/.pkgs")" "certbot python3-certbot-apache" "certbot plugin for the web server"
rm -f "$SD_ROOT/.pkgs"
o="$(R prepare --install web-nginx --web nginx)"; eq "$(code "$o")" 0 "prepare nginx exit"; has "$(cat "$SD_ROOT/.pkgs")" "nginx" "nginx package"
o="$(R prepare --install runtime --runtime 9)"; eq "$(code "$o")" 0 "runtime 9 (already visible) installs fine"
o="$(R prepare --install runtime,rsync --runtime 10)"; eq "$(code "$o")" 31 "runtime that does not appear is reported"; has "$o" "##KV failed=runtime" "failed list"; has "$o" "##STEP 2/2" "later items are still tried"
o="$(R prepare --install runtime)"; eq "$(code "$o")" 31 "runtime without a version fails"
o="$(R prepare --install 'web;rm')"; eq "$(code "$o")" 10 "install list is validated"
o="$(R prepare)"; eq "$(code "$o")" 10 "nothing to install"
o="$(R prepare --install bogus)"; eq "$(code "$o")" 31 "unknown item fails"

echo "== https (certificate for an existing site)"
cat > "$STUBS/certbot" <<'S'
#!/bin/sh
echo "$*" >> "$SD_ROOT/.certbot"
case "$*" in *bad.example.com*) echo "Challenge failed for domain bad.example.com"; exit 1 ;; esac
exit 0
S
chmod +x "$STUBS/certbot"; rm -f "$SD_ROOT/.certbot"
o="$(R https --domain shop.example.com --web apache)"
eq "$(code "$o")" 0 "https exit"; has "$o" "##KV https=ok" "https ok"; has "$(cat "$SD_ROOT/.certbot")" "--register-unsafely-without-email" "no email still works"
rm -f "$SD_ROOT/.certbot"
o="$(R https --domain shop.example.com --web nginx --email me@example.com)"; has "$o" "##KV https=ok" "https ok with email"; has "$(cat "$SD_ROOT/.certbot")" "-m me@example.com" "email passed"; has "$(cat "$SD_ROOT/.certbot")" "--nginx" "web plugin"
o="$(R https --domain bad.example.com --web apache)"
eq "$(code "$o")" 0 "failed certificate is not a script failure"; has "$o" "##KV https=pending" "pending"; has "$o" "Challenge failed" "certbot output is shown"; has "$o" "##WARN The certificate could not be issued" "warning"
o="$(R https --web apache)"; eq "$(code "$o")" 10 "https needs a domain"

echo "== site (recreate the site of an existing API)"
rm -f "$SD_ROOT/etc/nginx/conf.d/blog-api.conf" "$SD_ROOT/.certbot"
o="$(R site --svc blog-api --domain blog.example.com --web nginx --https yes)"
eq "$(code "$o")" 0 "site exit"; has "$o" "##KV https=ok" "site certificate"; has "$o" "##KV site=blog.example.com" "site kv"
has "$(cat "$SD_ROOT/etc/nginx/conf.d/blog-api.conf")" "server_name blog.example.com" "site server_name"
has "$(cat "$SD_ROOT/etc/nginx/conf.d/blog-api.conf")" "proxy_pass http://127.0.0.1:5060" "site port taken from the settings"
o="$(R preflight --svc blog-api)"; has "$o" "##KV domain=blog.example.com" "the new site is found as the domain"
o="$(R site --svc nope-api --domain x.example.com --web nginx)"; eq "$(code "$o")" 11 "site needs an existing service"
o="$(R site --svc blog-api --web nginx)"; eq "$(code "$o")" 10 "site needs a domain"
printf '#!/bin/sh\necho "AH00526: Syntax error on line 3 of the test"\nexit 1\n' > "$STUBS/apachectl"; chmod +x "$STUBS/apachectl"
before="$(cat "$SD_ROOT/etc/httpd/conf.d/shop-api.conf")"
o="$(R site --svc shop-api --domain shop2.example.com --web apache)"
eq "$(code "$o")" 30 "rejected configuration fails"; has "$o" "AH00526" "the reason is shown"
eq "$(cat "$SD_ROOT/etc/httpd/conf.d/shop-api.conf")" "$before" "previous site is put back"
printf '#!/bin/sh
exit 0
' > "$STUBS/apachectl"
printf 'ASPNETCORE_URLS=http://127.0.0.1:5070\n' > "$RUN/p4.env"
o="$(R provision --svc mail-api --dll M.dll --domain mail.example.com --port 5070 --web apache --https yes --run-dir "$RUN" --env-file "$RUN/p4.env")"
eq "$(code "$o")" 0 "provision with https and no email"; has "$o" "##KV https=ok" "provision issues the certificate without an email"

echo; echo "$PASS passed, $FAIL failed"
rm -rf "$T"
[ "$FAIL" = 0 ]
