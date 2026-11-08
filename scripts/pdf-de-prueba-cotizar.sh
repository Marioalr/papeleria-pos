#!/bin/bash
# Los PDF de prueba para «Cotizar contra imprimir» (docs/pruebas-en-tienda.md, 2026-11-08), en la PC de desarrollo (Linux, con
# Ghostscript): se arman entre semana y se llevan en la memoria USB. Ninguno tiene datos de un cliente.
#
#   scripts/pdf-de-prueba-cotizar.sh [carpeta]     (por omisión, ~/.cache/pdf-cotizar)
#
# El formulario llenado (6-formulario.pdf) no se arma aquí: uno de prueba, sin datos reales, si hay a la mano.
set -euo pipefail
salida="${1:-$HOME/.cache/pdf-cotizar}"
mkdir -p "$salida"
trabajo="$(mktemp -d)"
trap 'rm -rf "$trabajo"' EXIT

texto="Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna"

# Texto corrido en Helvetica 11 (se dibuja con Arial en Windows), de 50 a 742 puntos de alto; $1 = ancho y alto de la página.
pagina_de_texto() {
  local alto=$2
  echo "/Helvetica findfont 11 scalefont setfont 0 setgray"
  local y=$((alto - 60))
  while [ $y -gt 60 ]; do
    echo "60 $y moveto ($texto) show"
    y=$((y - 14))
  done
  echo "showpage"
}

pdf() { # $1 = nombre, $2 = ancho, $3 = alto (puntos), lo demás: el PostScript por la entrada
  gs -q -dBATCH -dNOPAUSE -dSAFER -sDEVICE=pdfwrite -dDEVICEWIDTHPOINTS="$2" -dDEVICEHEIGHTPOINTS="$3" -dFIXEDMEDIA \
     -sColorConversionStrategy=LeaveColorUnchanged -sOutputFile="$salida/$1" "$trabajo/$1.ps"
}

{ pagina_de_texto 612 792; pagina_de_texto 612 792; } > "$trabajo/1-texto.pdf.ps"
pdf 1-texto.pdf 612 792

cat > "$trabajo/2-mixto.pdf.ps" <<'EOF'
/Helvetica-Bold findfont 18 scalefont setfont 0 setgray 60 720 moveto (Reporte de prueba) show
% El logo a color.
1 0 0 setrgbcolor 470 700 60 60 rectfill 0 0.4 1 setrgbcolor 500 690 50 40 rectfill
% Una tabla con encabezado gris y renglones alternados.
/Helvetica findfont 10 scalefont setfont
0 1 14 { /i exch def
  i 0 eq { 0.7 setgray } { i 2 mod 0 eq { 0.92 setgray } { 1 setgray } ifelse } ifelse
  60 640 i 20 mul sub 492 20 rectfill
  0 setgray 66 646 i 20 mul sub moveto (Renglon de la tabla con un importe        $ 1,234.50) show
} for
/Helvetica findfont 11 scalefont setfont
60 300 moveto (Texto de cierre del reporte, en negro, como lo que mas se imprime.) show
showpage
EOF
pdf 2-mixto.pdf 612 792

# Una «foto»: un degradado de colores a toda la hoja, con margen (sin imagen de nadie). Cada renglón cambia con su altura.
cat > "$trabajo/3-foto.pdf.ps" <<'EOF'
gsave 36 36 translate 540 720 scale
/renglon 900 string def /y 0 def
300 400 8 [300 0 0 -400 0 400]
{ 0 1 299 { /x exch def
    renglon x 3 mul x 255 mul 299 div cvi put
    renglon x 3 mul 1 add y 255 mul 399 div cvi put
    renglon x 3 mul 2 add 255 x 255 mul 299 div cvi sub put } for
  /y y 1 add def renglon }
false 3 colorimage
grestore showpage
EOF
pdf 3-foto.pdf 612 792

cat > "$trabajo/4-lleno.pdf.ps" <<'EOF'
0 setgray 0 0 612 792 rectfill showpage
0.2 0.6 0.9 setrgbcolor 0 0 612 792 rectfill showpage
EOF
pdf 4-lleno.pdf 612 792

pagina_de_texto 595 842 > "$trabajo/5-a4.pdf.ps"
pdf 5-a4.pdf 595 842

cat > "$trabajo/7-cmyk.pdf.ps" <<'EOF'
/Helvetica findfont 11 scalefont setfont 0 0 0 1 setcmykcolor
60 700 moveto (Texto en negro CMYK: solo K al 100 por ciento.) show
0.6 0.4 0.4 1 setcmykcolor 60 500 300 120 rectfill
0 0 0 1 setcmykcolor 60 470 moveto (Arriba, un recuadro en negro rico: C60 M40 Y40 K100.) show
showpage
EOF
pdf 7-cmyk.pdf 612 792

ls -l "$salida"
