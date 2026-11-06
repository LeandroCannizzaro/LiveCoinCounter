# LiveCoinCounter

App Android che conta le monete euro su un tavolo e ne calcola il totale. Sul telefono si chiama **Conteggio Monete** (il progetto e il repository si chiamano LiveCoinCounter).

Il conteggio è una **sessione di riconoscimento continuo**: si tocca ▶ e si muove il telefono sopra il tavolo; l'app compone i fotogrammi in un **mosaico** dell'intera superficie (anche un tavolo intero, senza zone doppie) e intanto, su un altro thread, trova e ritaglia ogni moneta. Con ■ l'acquisizione si ferma, ogni ritaglio passa al riconoscitore del valore e compare il riepilogo. Ogni sessione resta salvata (database SQLite sul telefono) con mosaico, monete, valori e confidenze. Piano e decisioni: issue #6.

Il riconoscimento del valore può funzionare in tre modi, selezionabili nelle impostazioni. Il metodo predefinito è **Offline · YOLO11s 640**:

- **Offline, con un modello addestrato** (YOLO11 in formato ONNX): gira sul telefono, senza rete e senza inviare la foto a nessuno. È il metodo su cui si sta lavorando di più.
- **AI in rete**: la foto è inviata a un provider (Google Gemini, Anthropic Claude, OpenAI, xAI Grok, DeepSeek, Mistral, OpenRouter o un endpoint OpenAI-compatibile personalizzato).
- **Offline euristico (sperimentale)**: visione classica basata su dimensione e colore delle monete, senza modello. Meno accurato degli altri.

> **Stato del progetto.** I numeri di accuratezza riportati sotto sono misurati su foto di dataset pubblici, non su foto fatte con il tuo telefono: indicano che la pipeline funziona, **non** che il riconoscimento sia affidabile in ogni condizione. Il test con foto proprie è ancora da fare (vedi [Stato e prossimi passi](#stato-e-prossimi-passi)).

## Indice

- [Funzioni](#funzioni)
- [Riconoscimento continuo e sessioni](#riconoscimento-continuo-e-sessioni)
- [Installare l'app](#installare-limapp)
- [Come si usa](#come-si-usa)
- [Metodi di riconoscimento](#metodi-di-riconoscimento)
- [Modelli offline e risultati](#modelli-offline-e-risultati)
- [Struttura del repository](#struttura-del-repository)
- [Compilare dai sorgenti](#compilare-dai-sorgenti)
- [Aggiungere un modello offline](#aggiungere-un-modello-offline)
- [Addestramento del modello](#addestramento-del-modello)
- [Pipeline GitHub (build e release)](#pipeline-github-build-e-release)
- [Privacy](#privacy)
- [Dati e licenze](#dati-e-licenze)
- [Stato e prossimi passi](#stato-e-prossimi-passi)

## Funzioni

- **Riconoscimento continuo**: mosaico del tavolo che cresce sullo schermo, riquadro su ogni moneta trovata, pulsante trasparente ▶ / ■.
- **Sessioni salvate**: elenco, riepilogo, elenco delle monete con ritaglio, valore e confidenza, correzione manuale, rinomina, eliminazione, esportazione zip.
- Importazione di una foto dalla galleria (diventa una sessione con un solo fotogramma).
- Conteggio per taglio (1, 2, 5, 10, 20, 50 centesimi, 1 €, 2 €) e totale in euro.
- Più modelli offline selezionabili, con la possibilità di aggiungerne altri senza scrivere codice.
- Riquadri sulle monete riconosciute e **avvisi** quando il modello è incerto (monete poco riconoscibili, monete di taglio diverso che si sovrappongono).
- **AI in rete sulle monete incerte** (su richiesta, mai, o in automatico: si sceglie nelle impostazioni); all'AI vanno solo i ritagli delle singole monete.
- Raccolta facoltativa (spenta di default) delle foto analizzate e dei risultati, per migliorare il modello: le foto restano sul telefono finché non le esporti tu.
- Chiavi API salvate nello storage sicuro di Android; ritentativi automatici sugli errori di rete (cambio Wi-Fi / dati mobili).

## Installare l'app

Requisiti: telefono Android **7.0 (API 24) o successivo**.

1. Apri la pagina [Releases](../../releases) del repository e scarica `LiveCoinCounter-<versione>.apk` sul telefono (l'app installata si chiama «Conteggio Monete»).
2. Aprilo e, quando Android lo chiede, consenti l'installazione da questa origine («Installa app sconosciute»).
3. Alla prima analisi concedi il permesso della fotocamera, se vuoi scattare dal vivo.

L'APK è firmato con una chiave fissa: le versioni successive si installano sopra quelle precedenti senza perdere le impostazioni.

> Il repository è **privato**, quindi le release sono visibili solo agli account con accesso. Con il repository privato un link di download diretto non funziona senza autenticazione: scarica dal sito di GitHub (o con `gh release download`) dopo aver effettuato l'accesso.

## Come si usa

1. Metti le monete sul tavolo, **senza sovrapporle**, con una luce il più possibile uniforme. Durante la sessione non vanno spostate.
2. Nella scheda **Conta** tocca **▶**, tieni il telefono parallelo al tavolo a 25–40 cm e muovilo **lentamente**: il mosaico cresce sullo schermo, ogni moneta trovata riceve un riquadro (grigio = vista una volta, verde = confermata) e in alto c'è il numero di monete.
3. Quando hai coperto tutto tocca **■**: l'app analizza le ultime monete, ne riconosce il valore e apre il riepilogo.
4. Nel riepilogo **Vedi tutte le monete** mostra ritaglio, valore e confidenza di ognuna; toccane una per correggerla. Se ci sono monete incerte puoi riprovarle con l'AI.
5. Le sessioni passate sono nella scheda **Sessioni** (⋯ per rinominare, esportare o eliminare). Il pulsante 🖼 nella scheda Conta analizza invece una foto della galleria.

## Riconoscimento continuo e sessioni

Durante l'acquisizione lavorano due thread, collegati da code (`System.Threading.Channels`):

1. **Tracking e mosaico.** Ogni fotogramma viene collocato sul piano del tavolo con un'omografia; quando porta abbastanza superficie nuova (e non è mosso) diventa **fotogramma chiave** ed è proiettato nel mosaico a tessere. Dove i fotogrammi si sovrappongono vince il pixel migliore (più nitido, più lontano dal bordo): nessuna zona doppia.
2. **Rilevamento.** Su ogni fotogramma chiave il modello trova le monete (solo localizzazione). Le monete che toccano il bordo si scartano (compariranno intere altrove); le altre si portano nelle coordinate del mosaico e si **deduplicano**, così ogni moneta fisica è una sola riga nel database, con il ritaglio dalla sua osservazione migliore.

Dopo lo stop ogni ritaglio passa al classificatore (oggi lo stesso YOLO sul ritaglio, con il taglio del rilevatore come seconda opinione), poi un controllo sul **diametro relativo** (tutte le monete del mosaico hanno la stessa scala) abbassa la confidenza o corregge il taglio delle monete incoerenti.

Impostazioni (scheda Impostazioni → Riconoscimento continuo):

| Impostazione | Valori (predefinito in grassetto) |
|---|---|
| Metodo di composizione | **OpenCV** (punti ORB, tutti i telefoni) · ARCore (posa e piano del tavolo, scala in mm) · Ibrido (ARCore + affinamento OpenCV). Senza ARCore si ripiega su OpenCV. |
| Archivio delle sessioni | **Mosaico e ritagli** · Anche i fotogrammi chiave · Fotogrammi in debug (registra 3 fotogrammi al secondo con la posa) |
| Riconoscimento dopo lo stop | **Sul telefono, AI su richiesta** · Solo sul telefono · AI automatica sulle incerte |

Il codice senza dipendenze da Android (omografie, RANSAC, ORB in C#, mosaico, tracker, deduplica, motore, database, classificazione) è in `MobileApp/Recognition.Core` ed è coperto dai test di `MobileApp/Recognition.Core.Tests`, che fanno scorrere una fotocamera virtuale su un tavolo sintetico.

## Metodi di riconoscimento

| Metodo | Dove gira | Rete | Note |
|---|---|---|---|
| Offline · YOLO11s 640 (consigliato) | Telefono | No | Equilibrio tra precisione e velocità. |
| Offline · YOLO11n 960 | Telefono | No | Più piccolo ma a risoluzione maggiore: un po' più preciso con ombre e monete piccole, più lento. |
| Google Gemini, Anthropic Claude, OpenAI, Grok, DeepSeek, Mistral, OpenRouter | Provider | Sì | Serve una chiave API (per Gemini c'è un piano gratuito). Alcuni provider potrebbero non accettare immagini: se ricevi un errore scegli un altro. |
| Endpoint personalizzato | Tuo server | Sì | OpenAI-compatibile (Ollama, LM Studio, vLLM...). Richiede HTTPS: l'app blocca il traffico in chiaro. |
| Offline euristico (sperimentale) | Telefono | No | Misura i diametri e il colore delle monete; confonde monete sovrapposte e tagli dal diametro vicino. |

Come lavora il modello offline: la foto viene portata in un quadrato (con bordi grigi, «letterbox») della risoluzione del modello, il modello trova ogni moneta e ne dice direttamente il taglio, poi un passaggio di NMS indifferente alla classe evita di contare due volte la stessa moneta con tagli diversi. La soglia di confidenza è 0,25. Il risultato è la somma dei valori in centesimi.

I provider AI ricevono la foto ridimensionata e un prompt che chiede un JSON con i conteggi per taglio; in caso di errore di rete l'app ritenta con una connessione nuova e poi passa ai modelli di riserva del provider.

## Modelli offline e risultati

I modelli sono YOLO11 addestrati su 8 classi (i tagli dell'euro) e inclusi nell'app come asset ONNX in `MobileApp/LiveCoinCounter/Resources/Raw/`.

Percentuale di foto in cui il **totale è esatto** (soglia 0,25), per i cinque esperimenti di addestramento su Kaggle (metriche complete in `MachineLearning/training/results/kaggle_out/metrics.json`):

| Esperimento | val | test | val con ombre | test con ombre | mAP50-95 (test) | Minuti |
|---|---|---|---|---|---|---|
| n_base (YOLO11n, 640) | 96,7% | 87,1% | 96,7% | 85,5% | 0,868 | 15 |
| n_photo (+ ombre e luce in addestramento) | 96,7% | 87,1% | 93,4% | 88,7% | 0,869 | 11 |
| n_aug (+ foto sintetiche) | 95,1% | 90,3% | 98,4% | 90,3% | 0,871 | 41 |
| **s_aug** (YOLO11s, 640) | 98,4% | 93,5% | 98,4% | 93,5% | 0,877 | 46 |
| **n_aug_960** (YOLO11n, 960) | 98,4% | 93,5% | 98,4% | 95,2% | 0,876 | 86 |

I due in grassetto sono quelli inclusi nell'app. Cosa si ricava:

- Le foto sintetiche (monete ritagliate dal train e incollate su sfondi con ombre) portano il test da 87,1% a 90,3%; le sole augmentation fotometriche aiutano poco.
- Il modello più grande (`s_aug`) o la risoluzione maggiore (`n_aug_960`) portano entrambi a 93,5% sul test.
- **Val e test hanno circa 60 foto ciascuno**: la differenza tra 93,5% e 95,2% è una sola foto e non è un vantaggio dimostrato.
- I dati di val e test sono dei dataset pubblici, spesso più vicini al training di foto a caso (stesse monete, stessi sfondi): i numeri sono ottimistici rispetto a un telefono vero. Dettagli e cautele in `MachineLearning/training/RESULTS.md`.

Le versioni «con ombre» di val e test sono le stesse foto con ombre e luce non uniforme aggiunte da codice (`coin_ml.stress`): servono a misurare la robustezza.

## Struttura del repository

```
LiveCoinCounter/
├── MobileApp/
│   ├── LiveCoinCounter/        App .NET MAUI (Android): pagine, ViewModel, servizi, asset dei modelli
│   ├── Recognition.Core/       Libreria condivisa: provider AI, decodifica YOLO, mosaico (Mosaic/), sessioni e database (Sessions/)
│   └── Recognition.Core.Tests/ Test di mosaico, tracking, deduplica, database
├── MachineLearning/
│   ├── coin_ml/                Libreria Python: dataset, conversione in YOLO, foto sintetiche, ombre, metriche
│   ├── training/               Script Kaggle, risultati degli esperimenti (RESULTS.md)
│   ├── datasets/               Mappatura delle classi e note sulle fonti dati
│   ├── schema/                 Schema JSON delle etichette per immagine
│   ├── CoinLabeler/            Programma .NET che etichetta le foto con l'AI (Hough + provider + consenso)
│   ├── CoinLabeler.Tests/      Test .NET
│   └── tests/                  Test Python
└── .github/workflows/          Pipeline di build e release
```

Altri README: `MachineLearning/README.md` (dati e addestramento), `MachineLearning/datasets/README.md` (fonti dati), `MachineLearning/CoinLabeler/README.md` (etichettatore), `MachineLearning/training/RESULTS.md` (risultati e cautele).

## Compilare dai sorgenti

Servono .NET SDK 10, il workload MAUI per Android e un Android SDK (con Java 17 o successivo).

```bash
dotnet workload install maui-android

# Debug su telefono collegato in USB (debug USB attivo) o su emulatore
cd MobileApp/LiveCoinCounter
dotnet build -f net10.0-android -t:Run

# Test
dotnet test MobileApp/Recognition.Core.Tests/Recognition.Core.Tests.csproj
dotnet test MachineLearning/CoinLabeler.Tests/CoinLabeler.Tests.csproj
cd MachineLearning && python -m unittest discover tests
```

Per un APK di release firmato in locale:

```bash
keytool -genkeypair -v -keystore release.keystore -alias mia-chiave -keyalg RSA -keysize 2048 -validity 10000

export KS_PASS='la-tua-password'
dotnet publish MobileApp/LiveCoinCounter/LiveCoinCounter.csproj -f net10.0-android -c Release -o out \
  -p:AndroidPackageFormat=apk -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=release.keystore -p:AndroidSigningKeyAlias=mia-chiave \
  -p:AndroidSigningStorePass=env:KS_PASS -p:AndroidSigningKeyPass=env:KS_PASS
```

Il file `*-Signed.apk` in `out/` è quello da installare. **Non committare mai il keystore né la password.**

## Aggiungere un modello offline

Ogni modello sul dispositivo è un profilo del catalogo con il nome del suo file ONNX; la risoluzione di ingresso si legge dal file, quindi non serve altro codice.

1. Copia il `best.onnx` in `MobileApp/LiveCoinCounter/Resources/Raw/` (YOLO11 detect, le stesse 8 classi, uscita `[1, 4+8, ancore]`).
2. In `MobileApp/Recognition.Core/RecognizerProfile.cs` aggiungi a `RecognizerCatalog.All` un `RecognizerProfile` di tipo `RecognizerKind.OnDeviceModel` con `ModelAsset: "nome_del_file.onnx"`.

Il modello compare nell'elenco dei metodi nelle impostazioni. L'app tiene in memoria solo l'ultimo modello usato.

## Addestramento del modello

L'addestramento si fa su Kaggle (GPU gratuita); il PC locale serve per preparare i dati e provare gli script.

```bash
cd MachineLearning
python -m venv .venv && .venv\Scripts\activate     # Windows
pip install -r requirements.txt

# Unisce le sorgenti, raggruppa le foto simili e divide in train / val / test (80/10/10)
python -m coin_ml.make_dataset datasets/raw/kaggle-janstaffa datasets/processed/roboflow-euro-currency datasets/processed/combined

# Foto sintetiche con ombre, aggiunte solo al train
python -m coin_ml.synth datasets/processed/combined --count 1500
```

Poi si carica il dataset su Kaggle e si lancia `training/kaggle/kaggle_train.py`, che esegue gli esperimenti uno dopo l'altro e calcola le metriche che interessano (totale esatto per foto, su val e test, normali e con ombre). Procedura completa, comandi della CLI di Kaggle e cautele nella lettura dei numeri: `MachineLearning/training/RESULTS.md`.

Il programma `CoinLabeler` etichetta foto nuove con l'AI (cerchi con la trasformata di Hough + riconoscimento del taglio da più provider + consenso) e produce coppie immagine + JSON da rivedere e convertire in YOLO con `python -m coin_ml.to_yolo`.

## Pipeline GitHub (build e release)

Il workflow [`.github/workflows/build.yml`](.github/workflows/build.yml) fa tutto in GitHub Actions:

| Quando | Cosa |
|---|---|
| Push su `main`, pull request, avvio manuale | Test Python, test .NET e APK Android firmato, caricato come artefatto del run |
| Push di un tag `vX.Y.Z` | Come sopra, e poi crea la **release** con l'APK allegato |

Per pubblicare una nuova versione:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Il nome della versione dell'app viene dal tag; il numero interno di versione (quello che Android confronta per gli aggiornamenti) è il numero del run di GitHub Actions, quindi cresce a ogni build.

### Firma dell'APK

L'APK va firmato per potersi installare. La firma usa tre segreti del repository (Settings → Secrets and variables → Actions):

| Segreto | Contenuto |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | Il file keystore codificato in base64 |
| `ANDROID_KEYSTORE_PASSWORD` | La password del keystore (e della chiave) |
| `ANDROID_KEY_ALIAS` | L'alias della chiave |

```bash
base64 -w0 release.keystore | gh secret set ANDROID_KEYSTORE_BASE64
gh secret set ANDROID_KEYSTORE_PASSWORD
gh secret set ANDROID_KEY_ALIAS
```

Conserva il keystore e la password **fuori dal repository e in un posto sicuro**: se li perdi, le versioni future non potranno aggiornare quelle già installate (andrebbe disinstallata l'app). Senza i segreti, le build normali producono comunque un APK firmato con la chiave di debug (si installa, ma non si aggiorna con le versioni firmate ufficialmente); per un tag `vX.Y.Z` invece il workflow si ferma con un errore, così non esce mai una release con la firma sbagliata.

## Privacy

- Con i modelli **offline** la foto non lascia mai il telefono.
- Nelle sessioni l'AI in rete (se la usi) riceve **solo i ritagli** delle monete incerte, mai i fotogrammi o il mosaico; con «Solo sul telefono» non parte nessuna richiesta.
- Sessioni, mosaici e ritagli restano nella cartella privata dell'app; escono solo se usi «Esporta».
- Con i metodi **AI in rete** la foto ridimensionata è inviata al provider scelto, che la tratta secondo le sue condizioni; l'app non la salva.
- Le chiavi API sono nello storage sicuro di Android, non in chiaro.
- La raccolta delle foto per migliorare il modello è **spenta di default**; se la attivi, le foto restano in una cartella dell'app finché non scegli tu «Esporta foto raccolte».

## Dati e licenze

L'addestramento usa dataset pubblici di monete euro con box in formato YOLO:

| Fonte | Licenza | Uso |
|---|---|---|
| Kaggle `janstaffa/euro-coins-dataset` | Apache-2.0 (dichiarata da Kaggle) | 150 foto, usate |
| Roboflow Universe `euro-currency/euro-coin-detection-tfngl` (v3) | **CC BY 4.0**, autore «Euro Currency» | 459 foto originali, usate (copie aumentate rimosse) |

Altre fonti sono state valutate e **scartate** perché senza licenza dichiarata (dettagli in `MachineLearning/datasets/README.md`).

Librerie per il riconoscimento continuo: OpenCV (Apache-2.0, da Maven `org.opencv:opencv`), Google ARCore (`com.google.ar:core`, [termini aggiuntivi ARCore](https://developers.google.com/ar/develop/terms)), AndroidX CameraX, sqlite-net. L'APK di release include solo le librerie native arm64 (telefoni a 64 bit); le build di debug anche x64, per l'emulatore.

I modelli sono addestrati con [Ultralytics](https://github.com/ultralytics/ultralytics) YOLO11, rilasciato con licenza AGPL-3.0: se distribuisci l'app o i modelli, verifica che l'uso sia compatibile con quella licenza (o con una licenza commerciale Ultralytics).

Questo repository non ha ancora un file di licenza per il codice: finché non ne viene aggiunto uno, tutti i diritti sono riservati.

## Stato e prossimi passi

- [x] App Android con più metodi di riconoscimento, anche offline
- [x] Dataset pubblici importati, deduplicati e divisi per gruppi di foto simili
- [x] Addestramento su Kaggle, foto sintetiche con ombre, banchi di prova
- [x] Più modelli offline selezionabili, estensibile
- [x] Pipeline GitHub Actions: test, APK firmato, release
- [x] Riconoscimento continuo con mosaico, sessioni e database (issue #6)
- [ ] Provare i tre metodi di composizione su un tavolo vero e scegliere il predefinito (spike della issue #6)
- [ ] Rilevatore a una classe e classificatore dedicato sul ritaglio (issue #5)
- [ ] Provare i modelli su telefoni veri (velocità, memoria, conteggi) e scegliere il predefinito
- [ ] Raccogliere **foto proprie** (con la raccolta facoltativa dell'app) e usarle come test riservato: è l'unico numero che dice quanto il modello vale davvero
- [ ] Val e test più grandi, per distinguere meglio i modelli

Il piano e la discussione sono nelle issue del repository.
