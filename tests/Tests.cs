// Tests de la logique sans matériel : pas de luminosité, courbe et lissage de la luminosité auto.
// Lancés par : powershell -ExecutionPolicy Bypass -File build.ps1 -Test
using System;

namespace LGBrightnessHotkeys
{
    static class Tests
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok    " : "ÉCHEC ") + what);
            if (!ok) failures++;
        }

        // Une mesure par seconde, comme dans l'application.
        static void Feed(AutoBrightness a, double lux, int seconds)
        {
            for (int s = 0; s < seconds; s++) a.AddSample(lux);
        }

        static AutoBrightness Settled(double lux)
        {
            var a = new AutoBrightness();
            Feed(a, lux, 11);
            return a;
        }

        static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // 32 crans géométriques de 400 à 54000.
            Check(LgMonitor.LevelRaw(0) == LgMonitor.RawMin && LgMonitor.LevelRaw(LgMonitor.Steps) == LgMonitor.RawMax, "crans : de 400 à 54000");
            bool even = true, roundTrip = true;
            for (int k = 1; k <= LgMonitor.Steps; k++)
            {
                double ratio = (double)LgMonitor.LevelRaw(k) / LgMonitor.LevelRaw(k - 1);
                if (Math.Abs(ratio - 1.166) > 0.005) even = false;
                if (LgMonitor.NextStep(LgMonitor.LevelRaw(k - 1), 1) != LgMonitor.LevelRaw(k)) roundTrip = false;
                if (LgMonitor.NextStep(LgMonitor.LevelRaw(k), -1) != LgMonitor.LevelRaw(k - 1)) roundTrip = false;
            }
            Check(even, "chaque cran multiplie la luminosité par ≈ 1,166");
            Check(roundTrip, "d'un cran, + et − mènent aux crans voisins");

            // 10 % (valeur des tests précédents) tombe sur un cran : 8,6 % ↔ 10 % ↔ 11,7 %.
            int ten = LgMonitor.PercentToRaw(10);
            Check(LgMonitor.RawToPercent(LgMonitor.NextStep(ten, 1)) == 12, "10 % + → 12 % (11,7)");
            Check(LgMonitor.RawToPercent(LgMonitor.NextStep(ten, -1)) == 9, "10 % − → 9 % (8,6)");

            // Hors cran (après le mode auto) : on va au cran suivant ou précédent, sans sauter de cran.
            int t23 = LgMonitor.PercentToRaw(23);
            Check(LgMonitor.RawToPercent(LgMonitor.NextStep(t23, 1)) == 25, "23 % + → 25 % (25,2)");
            Check(LgMonitor.RawToPercent(LgMonitor.NextStep(t23, -1)) == 22, "23 % − → 22 % (21,6)");

            Check(LgMonitor.NextStep(LgMonitor.RawMax, 1) == LgMonitor.RawMax, "100 % + reste à 100 %");
            Check(LgMonitor.NextStep(LgMonitor.RawMin, -1) == LgMonitor.RawMin, "0 % − reste à 0 %");
            int raw = LgMonitor.RawMin, presses = 0;
            while (raw < LgMonitor.RawMax && presses < 100) { raw = LgMonitor.NextStep(raw, 1); presses++; }
            Check(presses == LgMonitor.Steps, "de 0 à 100 % en " + presses + " appuis");

            // Courbe : passe par ses points et ne redescend jamais quand la lumière augmente.
            Check(Math.Abs(AutoBrightness.Curve(0) - 5) < 1e-9, "courbe : 0 lux → 5 %");
            Check(Math.Abs(AutoBrightness.Curve(100) - 30) < 1e-9, "courbe : 100 lux → 30 %");
            Check(Math.Abs(AutoBrightness.Curve(4096) - 100) < 1e-9, "courbe : 4096 lux (saturation du capteur) → 100 %");
            bool monotonic = true;
            for (int lux = 1; lux <= 5000; lux++) if (AutoBrightness.Curve(lux) < AutoBrightness.Curve(lux - 1)) monotonic = false;
            Check(monotonic, "courbe croissante de 0 à 5000 lux");

            // Réglage manuel : la luminosité choisie devient la cible pour la lumière actuelle.
            var a = Settled(40);
            a.Learn(10);
            Check(a.Target() == 10, "après réglage à 10 % sous 40 lux, la cible est 10 %");

            // Variations mesurées avec la barre LED (42 → 33 lux) : la cible bouge de moins que le seuil.
            var led = Settled(42);
            led.Learn(10);
            Feed(led, 33, 60);
            Check(Math.Abs(led.Target() - 10) < AutoBrightness.Threshold, "barre LED éteinte (42 → 33 lux) : pas de changement (" + led.Target() + " %)");

            // Main devant le capteur ou flash de téléphone pendant 3 s, mesurés au test : pas de changement.
            var hand = Settled(40);
            hand.Learn(10);
            Feed(hand, 0, 3);
            Check(hand.Target() == 10, "main devant le capteur 3 s : pas de changement (" + hand.Target() + " %)");
            Feed(hand, 4096, 3);
            Check(hand.Target() == 10, "puis flash 3 s : pas de changement (" + hand.Target() + " %)");

            // Vraie hausse de lumière (40 → 500 lux) : la cible change au bout de 6 s.
            var day = Settled(40);
            day.Learn(10);
            int seconds = 0;
            while (day.Target() - 10 < AutoBrightness.Threshold && seconds < 120) { day.AddSample(500); seconds++; }
            Check(seconds == 6, "40 → 500 lux : la cible change après " + seconds + " s");
            Check(day.Target() == 52, "500 lux avec la préférence apprise (−8,6 points) : cible " + day.Target() + " %");

            // Bornes de la cible.
            var high = Settled(3000);
            high.Learn(100);
            Feed(high, 4096, 11);
            Check(high.Target() == 100, "cible plafonnée à 100 %");
            var low = Settled(40);
            low.Learn(0);
            Feed(low, 0, 11);
            Check(low.Target() == 0, "cible plancher à 0 %");

            Console.WriteLine(failures == 0 ? "Tous les tests passent." : failures + " test(s) en échec.");
            return failures == 0 ? 0 : 1;
        }
    }
}
