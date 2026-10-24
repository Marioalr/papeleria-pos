"""La marca de la prueba de instalación (.github/workflows/instalacion.yml) en la base del servicio, para ver que sobrevive a
actualizar y a regresar de una actualización que no arrancó, y qué pasa con ella al restaurar un respaldo.

    python marca.py poner <pos.db> [texto]            la escribe (con el servicio en marcha, como una venta)
    python marca.py revisar <pos.db> [texto]          sale con 0 si está y con 1 si no
    python marca.py respaldar <origen.db> <destino.db>  una copia completa con la copia en línea de SQLite, como los respaldos
                                                      del servicio (también lo que siga en el -wal)

Sin texto, «antes de actualizar». Va en una tabla propia: el servicio no la conoce ni la toca, y sus migraciones tampoco.
"""
import sqlite3
import sys

TEXTO = "antes de actualizar"


def main() -> int:
    que, ruta = sys.argv[1], sys.argv[2]
    if que == "respaldar":
        destino = sys.argv[3]
        origen = sqlite3.connect(ruta, timeout=30)
        copia = sqlite3.connect(destino)
        try:
            origen.backup(copia)
        finally:
            copia.close()
            origen.close()
        print(f"Copia de {ruta} en {destino}")
        return 0
    texto = sys.argv[3] if len(sys.argv) > 3 else TEXTO
    conexion = sqlite3.connect(ruta, timeout=30)
    try:
        if que == "poner":
            conexion.execute("CREATE TABLE IF NOT EXISTS MarcaDeLaPruebaDeInstalacion (Texto TEXT NOT NULL)")
            conexion.execute("INSERT INTO MarcaDeLaPruebaDeInstalacion (Texto) VALUES (?)", (texto,))
            conexion.commit()
            print(f"Marca '{texto}' puesta en {ruta}")
            return 0
        try:
            cuantas = conexion.execute(
                "SELECT COUNT(*) FROM MarcaDeLaPruebaDeInstalacion WHERE Texto = ?", (texto,)).fetchone()[0]
        except sqlite3.OperationalError as error:
            print(f"No esta la marca '{texto}' en {ruta}: {error}")
            return 1
        print(f"La marca '{texto}' esta en {ruta} ({cuantas})" if cuantas else f"No esta la marca '{texto}' en {ruta}")
        return 0 if cuantas else 1
    finally:
        conexion.close()


if __name__ == "__main__":
    sys.exit(main())
