# LG UltraFine Brightness Hotkeys

*English summary: a tiny, dependency-free Windows tray utility that changes the brightness of an LG UltraFine monitor in 32 perceptually even steps with a keyboard shortcut (Ctrl + Volume Up/Down by default, configurable). It talks to the monitor over USB HID, always starts from the monitor's actual brightness, shows a small on-screen indicator, and runs natively on Windows on ARM. Optional automatic brightness follows the monitor's own ambient light sensor and learns from your manual adjustments. Built from a single C# file with the compiler that ships with Windows.*

Petit utilitaire Windows qui règle la luminosité d'un écran **LG UltraFine** au clavier, sur 32 crans.

- **Ctrl + Volume +** / **Ctrl + Volume −** par défaut, modifiables.
- **Des crans fins en bas, larges en haut** : chaque appui multiplie la luminosité par environ 1,17, comme les crans de macOS. Exemples : … 8,6 → 10 → 11,7 → 13,7 → 15,9 % … 74 → 86 → 100 %. Chaque appui donne ainsi l'impression du même écart.
- Chaque appui part de la **luminosité réelle de l'écran**, relue en HID. Une valeur hors cran va au cran voisin : 23 % → 25 % ou 22 %.
- Maintenir la touche fait défiler.
- **Luminosité automatique** en option : elle suit le capteur de lumière de l'écran et apprend de vos réglages au clavier.
- Indicateur discret en bas de l'écran principal (☀ NN % et une barre), qui ne prend pas le focus.
- Icône dans la zone de notification : infobulle avec la luminosité, menu **Luminosité automatique**, **Raccourcis…** et **Quitter**.
- Un seul fichier C#, aucune dépendance, aucun SDK à installer. Natif ARM64 sur Windows on ARM.

## Pourquoi

L'app du Microsoft Store « LG Ultrafine Brightness » (v1.0.3) a des raccourcis globaux, mais sur un Windows réglé avec la virgule comme séparateur décimal (fr-FR, par exemple), ils partent toujours de 25 %. La luminosité est enregistrée avec un point (`0.3`) puis relue dans la culture courante. La relecture échoue et la valeur retombe sur le défaut. Résultat : les raccourcis n'alternent qu'entre 20 % et 30 %.

## Écrans

| Écran | Vendor ID | Product ID | Testé |
|---|---|---|---|
| LG UltraFine 5K | `0x043E` | `0x9A70` | oui |
| LG UltraFine 4K | `0x043E` | `0x9A40`, `0x9A63` | non |

L'utilitaire ouvre l'interface HID LG dont la page d'usage est **Monitor** (`0x80`, usage `0x01`). Il ne filtre pas sur le Product ID, donc les modèles 4K devraient fonctionner aussi.

## Compiler

Sous Windows 10 ou 11, avec .NET Framework 4.8 ou 4.8.1, présent par défaut :

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

L'exe est créé dans `build\LGBrightnessHotkeys.exe`. Avec `-Test`, le script compile et lance aussi les tests de la logique ([tests/Tests.cs](tests/Tests.cs)), qui n'ont pas besoin de l'écran.

## Installer

```powershell
$dest = "$env:LOCALAPPDATA\Programs\LGBrightnessHotkeys"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item build\LGBrightnessHotkeys.exe $dest
# Lancement à l'ouverture de session
Set-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name LGBrightnessHotkeys -Value "`"$dest\LGBrightnessHotkeys.exe`""
Start-Process "$dest\LGBrightnessHotkeys.exe"
```

Si l'app du Store est aussi installée, décochez son option « Global hotkeys », sinon les deux se disputent les raccourcis.

## Utiliser

- **Changer les raccourcis** : double-clic sur l'icône, menu **Raccourcis…**, ou relancer l'exe. Cliquez dans une case puis appuyez sur la combinaison voulue. Ctrl, Alt, Maj ou Win sont obligatoires, sauf pour F1–F24, Pause, Arrêt défil et les touches multimédia. Si une combinaison est déjà utilisée par une autre application ou réservée par Windows, la fenêtre l'indique. Les réglages sont enregistrés dans `HKCU\Software\LGBrightnessHotkeys`.
- **Raccourci déjà pris au démarrage** : une notification l'indique. Le menu propose alors **Réactiver les raccourcis**.
- **Luminosité automatique** : à cocher dans le menu de l'icône. À l'activation, la luminosité actuelle sert de référence pour la lumière actuelle. Ensuite :
  - quand la lumière de la pièce change durablement, la luminosité suit, en fondu de 1,5 s, sans indicateur à l'écran ;
  - les raccourcis restent actifs : un appui ne coupe pas le mode auto, il corrige votre préférence (« à cette lumière, je veux 5 % de plus ») ;
  - un réglage fait par un autre logiciel est adopté de la même façon ;
  - l'infobulle indique la lumière mesurée, par exemple « Luminosité LG : 35 % (auto, 120 lux) ».

  Le réglage reste actif au redémarrage de Windows.

## Désinstaller

Menu de l'icône → **Quitter**, puis :

```powershell
Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name LGBrightnessHotkeys
Remove-Item -Recurse "$env:LOCALAPPDATA\Programs\LGBrightnessHotkeys", HKCU:\Software\LGBrightnessHotkeys
```

## Détails techniques

### Protocole HID

| | |
|---|---|
| Lecture | `HidD_GetFeature`, rapport 0 de 7 octets. Valeur brute = `buf[1] \| buf[2] << 8` |
| Écriture | `HidD_SetFeature` avec `{0x00, lo, hi, 0, 0, 0, 0}` |
| Plage brute | `400` (0x0190) à `54000` (0xD2F0) |
| Conversion | `brut = round(% / 100 × 54000)`, minimum 400. Même convention que l'app LG : 20 % = 0x2A30, 25 % = 0x34BC, 30 % = 0x3F48 |

### Latence

L'écran ne renvoie la nouvelle valeur que 180 à 360 ms après une écriture. Pendant 1 s après chacune de ses propres écritures, l'utilitaire part donc de la valeur qu'il vient d'écrire. Sinon, des appuis rapprochés ou une touche maintenue perdraient des pas.

### Crans

Cran `k` (0 à 32) = `round(400 × 135^(k/32))`, où 135 = 54000 / 400. Il y a donc un même rapport (≈ 1,166) entre deux crans voisins. L'œil perçoit la luminosité de façon à peu près logarithmique, donc des pas égaux en pourcentage seraient énormes en bas et imperceptibles en haut.

macOS règle l'écran sur 16 crans, et 64 avec Option + Maj. Le projet [unknownzerx/lguf-brightness](https://github.com/unknownzerx/lguf-brightness) contient deux tables de 16 et 64 crans sur la même plage 400–54000, avec un rapport constant de 1,359 et 1,08. Leur origine n'y est pas documentée ; leur nombre correspond à macOS. 32 crans est un compromis : moins grossier que 16 en haut, plus rapide que 64 à parcourir.

### Capteur de lumière

L'UltraFine 5K a un capteur de lumière ambiante, exposé en USB comme capteur HID standard (interface `MI_02`). Windows le reconnaît sans pilote supplémentaire (« Capteur de lumière ambiante HID ») et l'utilitaire le lit par l'API `Windows.Devices.Sensors.LightSensor`, en choisissant le capteur dont l'identifiant contient `VID_043E`. Celui d'un portable est donc ignoré.

- Mesures relevées sur un 5K : 30 à 45 lux dans une pièce éclairée le soir, 0 lux main posée dessus, 4 096 lux (saturation) sous un flash de téléphone.
- Le capteur n'envoie une mesure que quand la lumière change. La dernière valeur reste valable tant que rien ne bouge.
- **Courbe** : 0 lux → 5 %, 10 → 10 %, 50 → 20 %, 100 → 30 %, 300 → 50 %, 1 000 → 75 %, 3 000 → 100 %, interpolée sur `log10(lux + 1)`. S'y ajoute le décalage appris de vos réglages, enregistré dans `AutoOffset` en dixièmes de point.
- **Stabilité** : une mesure par seconde, médiane des 11 dernières. Une main ou un flash de moins de 5 s ne change rien. La luminosité ne bouge que si l'écart avec la cible atteint 5 points.

L'API WinRT est appelée depuis .NET Framework sans SDK : `build.ps1` référence `Windows.Devices.winmd` et `Windows.Foundation.winmd` de `System32\WinMetadata`, ainsi que les façades `System.Runtime.dll` et `System.Runtime.InteropServices.WindowsRuntime.dll` du Framework.

### ARM64 natif

Un exe .NET Framework « AnyCPU » tourne par défaut en **émulation x64** sur Windows on ARM. `/platform:anycpu` ne suffit pas pour l'exécution native, ni l'attribut `TargetFramework 4.8.1`. Il faut l'élément `supportedArchitectures` du manifeste ([src/app.manifest](src/app.manifest)) :

```xml
<supportedArchitectures xmlns="http://schemas.microsoft.com/SMI/2024/WindowsSettings">amd64 arm64</supportedArchitectures>
```

C'est ce que produit la propriété MSBuild `PreferNativeArm64`. Pour vérifier l'architecture d'un processus, la colonne « Architecture » du Gestionnaire des tâches convient, ou `GetProcessInformation(ProcessMachineTypeInfo)`. `IsWow64Process2` ne détecte pas l'émulation x64.

### Diagnostic

[tools/hidprobe.ps1](tools/hidprobe.ps1) lit la luminosité de l'écran sans la modifier. Il peut aussi simuler des appuis : `-Keys "uuudd" -Gap 800` envoie Ctrl + Volume + trois fois puis Ctrl + Volume − deux fois, et affiche la valeur après chaque appui.

## Licence

[MIT](LICENSE)
