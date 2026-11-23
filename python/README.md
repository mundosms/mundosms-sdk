# mundosms-sdk

Cliente Python de la [API REST v3 de MundoSMS](https://www.mundosms.es/api-v3): SMS, WhatsApp, centralita, VozPush y
NumCheck. Firma cada petición por ti y reintenta solo lo que es seguro repetir. Sin dependencias: solo la biblioteca
estándar. Requiere Python 3.8 o superior.

```sh
pip install mundosms-sdk
```

El paquete se llama `mundosms-sdk` y el módulo, `mundosms`:

```python
from mundosms import MundoSMS
```

Las API Keys (token y clave privada) se crean en el panel de MundoSMS, en **API y Soporte → Claves de API**. Cómo se usa
el cliente, en el [README del repositorio](https://github.com/mundosms/mundosms-sdk#readme); las rutas y sus parámetros, en
la [documentación de la API](https://www.mundosms.es/api-v3).

Licencia MIT.
