#!/bin/sh
# Проверка на живом трафике (Linux, root): движок считает профиль с пустыми include-листами профилем «для всех».
# Страховочный --hostlist-domains должен удерживать пустой автосписок от обхода всего подряд.
# Использование: sudo tests/engine/guard-live.sh <путь к nfqws>
set -eu

NFQWS=${1:?путь к nfqws}
HOST=example.com
QNUM=200
TMP=$(mktemp -d)
chmod 755 "$TMP"
IP=$(getent ahostsv4 "$HOST" | awk 'NR==1 {print $1}')
[ -n "$IP" ] || { echo "не удалось разрешить $HOST"; exit 2; }

BLOCK="-s $IP -p tcp --sport 443 ! --tcp-flags SYN,ACK SYN,ACK -j DROP"
cleanup() {
	iptables -D INPUT $BLOCK 2>/dev/null || true
	iptables -t mangle -D OUTPUT -p tcp -d "$IP" --dport 443 -j NFQUEUE --queue-num $QNUM --queue-bypass 2>/dev/null || true
	[ -n "${PID:-}" ] && kill "$PID" 2>/dev/null || true
	rm -rf "$TMP"
}
trap cleanup EXIT

iptables -t mangle -I OUTPUT -p tcp -d "$IP" --dport 443 -j NFQUEUE --queue-num $QNUM --queue-bypass

# $1 - имя прогона, остальное - опции профиля со списками
run() {
	name=$1; shift
	: > "$TMP/auto.txt"; : > "$TMP/empty.txt"
	chmod 666 "$TMP/auto.txt" "$TMP/empty.txt"
	"$NFQWS" --qnum=$QNUM --debug --filter-tcp=443 "$@" \
		--dpi-desync=multisplit --dpi-desync-split-pos=1 > "$TMP/$name.log" 2>&1 &
	PID=$!
	sleep 1
	kill -0 "$PID" 2>/dev/null || { echo "FAIL $name: движок не запустился"; cat "$TMP/$name.log"; exit 1; }
	for _ in $(seq "${ATTEMPTS:-1}"); do
		curl -s -o /dev/null --noproxy "*" --max-time "${CURL_TIME:-15}" --resolve "$HOST:443:$IP" "https://$HOST/" || true
	done
	sleep 0.5
	kill "$PID"; wait "$PID" 2>/dev/null || true
	PID=
}

tampered() { grep -q 'sending multisplit part' "$TMP/$1.log"; }
skipped() { grep -q "include hostlist check for $HOST : negative" "$TMP/$1.log"; }

GUARD=--hostlist-domains=zapret-smart.invalid
run empty "--hostlist=$TMP/empty.txt"
run empty-guard "--hostlist=$TMP/empty.txt" "$GUARD"
run auto-guard "--hostlist-auto=$TMP/auto.txt" "$GUARD"

fail=0
expect() {
	# $1 - прогон, $2 - tampered|skipped, $3 - описание
	if [ "$2" = tampered ] && tampered "$1" && ! skipped "$1"; then echo "OK   $3"
	elif [ "$2" = skipped ] && skipped "$1" && ! tampered "$1"; then echo "OK   $3"
	else echo "FAIL $3"; echo "--- $1"; grep -E 'hostlist|tampering|multisplit|profile' "$TMP/$1.log" | tail -20; fail=1
	fi
}
expect empty tampered "контроль: пустой список без страховки = обход всего трафика ($HOST разрезан)"
expect empty-guard skipped "пустой список со страховкой: $HOST не тронут"
expect auto-guard skipped "автосписок со страховкой (как собирает приложение): $HOST не тронут"

# Имитация блокировки: после рукопожатия ответы сервера пропадают, ClientHello уходит в ретрансмиссии.
iptables -I INPUT $BLOCK
ATTEMPTS=3 CURL_TIME=6 run detect "--hostlist-auto=$TMP/auto.txt" "$GUARD"
if grep -qx "$HOST" "$TMP/auto.txt"; then echo "OK   автосписок: заблокированный $HOST добавлен движком сам"
else echo "FAIL автосписок: $HOST не добавлен"; grep -E 'auto hostlist|retrans' "$TMP/detect.log" | tail -15; fail=1; fi

echo "$HOST" > "$TMP/exclude.txt"
ATTEMPTS=3 CURL_TIME=6 run detect-excluded "--hostlist-auto=$TMP/auto.txt" "$GUARD" "--hostlist-exclude=$TMP/exclude.txt"
if [ ! -s "$TMP/auto.txt" ]; then echo "OK   исключения: $HOST из списка исключений в автосписок не попал"
else echo "FAIL исключения: $HOST попал в автосписок"; fail=1; fi
iptables -D INPUT $BLOCK

exit $fail
