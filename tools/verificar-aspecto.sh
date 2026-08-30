#!/usr/bin/env bash
#
# Verifica o aspecto (proporção) dos vídeos e detecta bordas pretas "chumbadas"
# no arquivo — a causa mais comum de série antiga aparecer com barras em todos
# os lados numa TV widescreen.
#
# Uso:
#   tools/verificar-aspecto.sh /caminho/da/midia
#   tools/verificar-aspecto.sh "/media/Serie X/Episodio 01.mkv"
#
# Legenda do veredito:
#   OK 16:9            -> preenche a TV widescreen, sem barras
#   4:3 / pillarbox    -> barras SÓ nas laterais (correto p/ conteúdo 4:3 antigo)
#   letterbox          -> barras SÓ em cima/baixo (correto p/ filme "scope")
#   BARRAS CHUMBADAS   -> o próprio arquivo tem preto gravado na imagem;
#                         numa TV widescreen vira barra em cima, baixo E dos lados
#   SAR anamórfico     -> pixel não-quadrado; player de TV antiga pode ignorar
#                         e exibir a imagem esticada/espremida ou emoldurada

set -u

alvo="${1:-/media}"

probe() {
  ffprobe -v error -select_streams v:0 \
    -show_entries stream=codec_name,width,height,sample_aspect_ratio,display_aspect_ratio,pix_fmt,field_order \
    -of default=nw=1:nk=0 "$1"
}

# roda cropdetect em 3 pontos do vídeo e devolve o maior recorte encontrado
detectar_crop() {
  local f="$1" dur pos i best_w=0 best_h=0 c cw ch
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f" 2>/dev/null)
  dur=${dur%.*}
  [ -z "$dur" ] || [ "$dur" -lt 10 ] 2>/dev/null && dur=60
  for i in 20 50 80; do
    pos=$(( dur * i / 100 ))
    c=$(ffmpeg -hide_banner -ss "$pos" -i "$f" -vf cropdetect=limit=24:round=2 \
          -frames:v 150 -an -f null - 2>&1 | grep -o 'crop=[0-9]*:[0-9]*' | tail -1)
    c=${c#crop=}
    cw=${c%%:*}; ch=${c##*:}
    [ -n "${cw:-}" ] && [ "${cw:-0}" -gt "$best_w" ] 2>/dev/null && { best_w=$cw; best_h=$ch; }
  done
  echo "${best_w}:${best_h}"
}

analisar() {
  local f="$1"
  local info w h sar dar
  info=$(probe "$f") || { echo "  !! ffprobe falhou (arquivo corrompido?)"; return; }
  w=$(sed -n 's/^width=//p' <<<"$info")
  h=$(sed -n 's/^height=//p' <<<"$info")
  sar=$(sed -n 's/^sample_aspect_ratio=//p' <<<"$info")
  dar=$(sed -n 's/^display_aspect_ratio=//p' <<<"$info")

  echo "  codec=$(sed -n 's/^codec_name=//p' <<<"$info")  ${w}x${h}  SAR=${sar:-?}  DAR=${dar:-?}  $(sed -n 's/^field_order=//p' <<<"$info")"

  [ -z "$w" ] && { echo "  veredito: ??? (sem stream de vídeo)"; return; }

  local ratio
  ratio=$(awk -v a="$w" -v b="$h" 'BEGIN{printf "%.3f", a/b}')

  # SAR anamórfico?
  case "$sar" in
    ""|"1:1"|"0:1"|"N/A") : ;;
    *) echo "  veredito: SAR anamórfico ($sar) — TV antiga pode exibir errado. Reencodar p/ pixel quadrado resolve." ;;
  esac

  # bordas chumbadas?
  local crop cw ch
  crop=$(detectar_crop "$f")
  cw=${crop%%:*}; ch=${crop##*:}
  if [ "${cw:-0}" -gt 0 ] 2>/dev/null; then
    local perda_w perda_h
    perda_w=$(( (w - cw) * 100 / w ))
    perda_h=$(( (h - ch) * 100 / h ))
    if [ "$perda_w" -ge 3 ] || [ "$perda_h" -ge 3 ]; then
      echo "  veredito: BARRAS CHUMBADAS no arquivo — imagem real é ${cw}x${ch} (perde ${perda_w}% larg / ${perda_h}% alt)."
      echo "            corrigir: reencodar com  -vf \"crop=${cw}:${ch}\"  (confira o valor antes)"
      return
    fi
  fi

  # aspecto "limpo"
  if awk -v r="$ratio" 'BEGIN{exit !(r>1.72 && r<1.82)}'; then
    echo "  veredito: OK 16:9 — preenche a TV."
  elif awk -v r="$ratio" 'BEGIN{exit !(r>1.28 && r<1.40)}'; then
    echo "  veredito: 4:3 — barras SÓ nas laterais na TV widescreen (correto p/ conteúdo antigo)."
  elif awk -v r="$ratio" 'BEGIN{exit !(r>=1.82)}'; then
    echo "  veredito: letterbox 'scope' — barras SÓ em cima/baixo (normal)."
  else
    echo "  veredito: aspecto incomum ($ratio:1)."
  fi
}

if [ -f "$alvo" ]; then
  echo "== $alvo"
  analisar "$alvo"
  exit 0
fi

find "$alvo" -type f \( -iname '*.mkv' -o -iname '*.mp4' -o -iname '*.avi' \
  -o -iname '*.m4v' -o -iname '*.webm' -o -iname '*.mov' -o -iname '*.ts' \) \
  | sort | while IFS= read -r f; do
  echo "== ${f#"$alvo"/}"
  analisar "$f"
done
