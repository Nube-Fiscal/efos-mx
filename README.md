# efos-mx

> Listado 69-B del SAT (EFOS) en JSON, actualizado diariamente y disponible gratis vía GitHub Pages.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## ¿Qué es esto?

El SAT publica el [listado de contribuyentes con operaciones presuntamente simuladas (Art. 69-B CFF)](https://www.gob.mx/sat) como un archivo CSV sin versionar, sin historial y sin API. Este proyecto:

1. **Descarga** el CSV oficial del SAT cada día a las 02:00 AM (hora de México)
2. **Parsea** y convierte a JSON estructurado con nombres de campo en español
3. **Genera un diff** diario: qué RFCs entraron y cuáles salieron del listado
4. **Publica** todo vía GitHub Pages — sin servidor, sin base de datos, sin costo

---

## Endpoints

| Archivo | Descripción |
|---|---|
| [`/metadata.json`](https://nubefiscal.github.io/efos-mx/metadata.json) | Fecha de actualización, total de registros y SHA256. Ideal para polling eficiente. |
| [`/listado.json`](https://nubefiscal.github.io/efos-mx/listado.json) | Listado completo de los ~14,000 contribuyentes. |
| `/diff/YYYY-MM-DD.json` | Cambios del día: RFCs agregados y eliminados. |

### Ejemplo: `metadata.json`

```json
{
  "ultima_actualizacion": "2026-06-06T08:00:00Z",
  "total_registros": 14426,
  "sha256": "558564...",
  "fuente": "https://...SAT.../Listado_completo_69-B.csv",
  "diff_disponible": "diff/2026-06-06.json"
}
```

### Ejemplo: entrada en `listado.json`

```json
{
  "rfc": "AAA010101AAA",
  "nombre": "EMPRESA SIMULADORA SA DE CV",
  "situacion": "Definitivo",
  "sat_presuncion": "15/01/2020",
  "dof_presuncion": "20/01/2020",
  "sat_definitivo": "15/03/2020",
  "dof_definitivo": "20/03/2020"
}
```

### Ejemplo: `diff/2026-06-06.json`

```json
{
  "fecha": "2026-06-06",
  "resumen": { "total_anterior": 14419, "total_actual": 14426, "agregados": 7, "eliminados": 0 },
  "agregados": [ { "rfc": "...", "nombre": "...", "situacion": "Presunto" } ],
  "eliminados": []
}
```

---

## Cliente .NET (NuGet)

Si consumes este endpoint desde una aplicación .NET, usa el paquete [`NubeFiscal.Efos`](https://www.nuget.org/packages/NubeFiscal.Efos):

```bash
dotnet add package NubeFiscal.Efos
```

```csharp
// Program.cs
builder.Services.AddEfosClient();

// En tu servicio
bool esEfos = await _efosClient.EsEfosAsync("AAA010101AAA");
EfosEntry? detalle = await _efosClient.ConsultarAsync(rfc);
```

El cliente solo descarga el listado completo cuando el SHA256 cambia — el resto del tiempo usa el cache en memoria.

---

## ¿Por qué F# y no C# o Python?

El corazón del proyecto es un script F# (`scripts/generate_json.fsx`) que corre con `dotnet fsi`, sin dependencias externas. Esta decisión no fue arbitraria:

### vs Python

| | Python | F# |
|---|---|---|
| **Disponibilidad en CI** | Requiere `actions/setup-python` con versión fija | `dotnet fsi` viene preinstalado en todos los runners de GitHub Actions |
| **Tipos** | Dinámico — errores de campo en runtime | Estático — el compilador atrapa errores antes de correr |
| **Encodings** | `open()` con `encoding=` correcto es responsabilidad tuya | La cadena de decoders con fallback es explícita y verificable |
| **Dependencias** | `pip install` o `requirements.txt` | Cero dependencias externas — solo BCL de .NET |

### vs C#

| | C# | F# |
|---|---|---|
| **Pipelines de datos** | Verboso con LINQ y lambdas | Natural con `\|>` — el flujo se lee de arriba a abajo |
| **Modelos inmutables** | `record` con mucho boilerplate | `type Entry = { Rfc: string; Nombre: string }` — inmutable por defecto |
| **Scripts** | No tiene equivalente a `.fsx` | `dotnet fsi script.fsx` — ejecutable directo sin proyecto |
| **Lenguaje unificado** | Script en Python/Bash + librería en C# | Script **y** librería en el mismo lenguaje |

El resultado: el script completo — descarga, parseo de CSV con detección de encoding, generación de diff y escritura de JSON — cabe en ~200 líneas sin ningún `using` de terceros.

---

## Cómo funciona internamente

```
[GitHub Actions - cron 08:00 UTC]
         │
         ▼
[dotnet fsi scripts/generate_json.fsx]
         │
         ├── Descarga CSV del SAT (urllib / HttpClient)
         ├── Detecta encoding: UTF-8 estricto → fallback ISO-8859-1
         ├── Encuentra la fila de headers (salta el preámbulo del SAT)
         ├── Mapea columnas con normalización NFD (resistente a cambios de nombre)
         ├── Calcula SHA256 del listado actual
         ├── Compara contra listado.json anterior → genera diff
         └── Escribe metadata.json / listado.json / diff/YYYY-MM-DD.json
                   │
                   ▼
         [git commit & push → main]
                   │
                   ▼
         [GitHub Pages publica docs/]
```

---

## Estructura del repo

```
efos-mx/
├── .github/workflows/
│   └── update-efos.yml     # Cron diario + trigger manual
├── scripts/
│   └── generate_json.fsx   # Script F# — toda la lógica de transformación
├── docs/                   # Raíz de GitHub Pages
│   ├── metadata.json
│   ├── listado.json
│   └── diff/
│       └── YYYY-MM-DD.json
└── LICENSE
```

---

## Fuente oficial

Los datos provienen del SAT y son de carácter público:
[Listado completo 69-B (CSV)](https://www.gob.mx/sat/acciones-y-programas/notificacion-a-contribuyentes-con-operaciones-presuntamente-inexistentes-y-listados-definitivos-333336)

---

Hecho con F# y GitHub Actions por [Nube Fiscal](https://nubefiscal.com.mx).
