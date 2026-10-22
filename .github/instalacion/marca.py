"""La marca de la prueba de instalación (.github/workflows/instalacion.yml) en la base del servicio, para ver que sobrevive a
actualizar y a regresar de una actualización que no arrancó.

    python marca.py poner <pos.db>     la escribe (con el servicio en marcha, como una venta)
    python marca.py revisar <pos.db>   sale con 0 si está y con 1 si no

Va en una tabla propia: el servicio no la conoce ni la toca, y sus migraciones tampoco.
"""
import sqlite3
import sys

TEXTO = "antes de actualizar"


def main() -> int:
    que, ruta = sys.argv[1], sys.argv[2]
    conexion = sqlite3.connect(ruta, timeout=30)
    try:
        if que == "poner":
            conexion.execute("CREATE TABLE IF NOT EXISTS MarcaDeLaPruebaDeInstalacion (Texto TEXT NOT NULL)")
            conexion.execute("INSERT INTO MarcaDeLaPruebaDeInstalacion (Texto) VALUES (?)", (TEXTO,))
            conexion.commit()
            print(f"Marca puesta en {ruta}")
            return 0
        try:
            cuantas = conexion.execute(
                "SELECT COUNT(*) FROM MarcaDeLaPruebaDeInstalacion WHERE Texto = ?", (TEXTO,)).fetchone()[0]
        except sqlite3.OperationalError as error:
            print(f"No esta la marca en {ruta}: {error}")
            return 1
        print(f"La marca esta en {ruta} ({cuantas})" if cuantas else f"No esta la marca en {ruta}")
        return 0 if cuantas else 1
    finally:
        conexion.close()


if __name__ == "__main__":
    sys.exit(main())
