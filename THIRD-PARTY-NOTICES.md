# Third-party notices

HassSharp is MIT-licensed (`LICENSE`). A release also carries the following, which are not HassSharp's own. Their
licences ship with the integration, in `custom_components/hass_sharp/licenses/`.

| What | Version | Where in a release | Licence | Licence file |
|---|---|---|---|---|
| DejaVu Sans, the font text is drawn on pictures with | 2.37 | embedded in `out/HassSharp.dll` | Bitstream Vera and Arev font licences; DejaVu's changes are in the public domain | `licenses/DejaVu-LICENSE.txt` |
| StbTrueTypeSharp, which draws that font's glyphs | 1.26.13 | `out/StbTrueTypeSharp.dll` | Unlicense or MIT (used under the Unlicense) | `licenses/StbTrueTypeSharp-LICENSE.txt` |

**libjpeg-turbo** decodes and encodes pictures. It is not shipped: HassSharp uses the system's `libturbojpeg`
(Home Assistant's image has it), and drawing on a picture is unavailable where the system has none.
