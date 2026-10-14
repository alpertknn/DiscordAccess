# DiscordAccess

Windows'ta kurulu resmi Discord Stable için sistem tepsisinde çalışan bağlantı yardımcısı. **v4.22**, masaüstü/indirme klasörünün izinleri nedeniyle kurulamayan Windows başlangıcını güvenli kişisel paket kopyasıyla düzeltir; mevcut dosya izinlerini ve kullanıcının başlangıcı kapatma tercihini korur.

`DiscordAccess.exe` bağlantıyı hazırlar; Discord'u kendi masaüstü veya Başlat menüsü simgenizden açarsınız. Kısayolun gerçek Discord hedefi korunur, yalnız oturumun yerel proxy argümanları eklenir. Ayrı Discord veya profil kopyası oluşturulmaz; mevcut hesabınız kullanılır. Açık CMD penceresi gerekmez.

## Hazır Windows paketini indir

[v4.22 Release sayfasından](https://github.com/alpertknn/DiscordAccess/releases/tag/v4.22) **DiscordAccess-v4.22.zip** dosyasını indirin. Desteklenen hazır paket en son yayımlanan sürümdür.

`DiscordAccess-v4.22-engine-source.zip` motorun karşılık gelen kaynakları ve yeniden derleme malzemeleridir; programı çalıştırmak için indirmeniz gerekmez. Hazır paketi yeniden dağıtıyorsanız bu kaynak paketini ve lisans bildirimlerini de sunun. Eski sürüm etiketleri kaynak geçmişi için korunur; önceki eksik bildirimli ikili paketleri yeniden dağıtmayın.

ZIP'i tamamen kısa, kalıcı bir yerel klasöre çıkarın. `DiscordAccess.exe` ve `engine/sing-box.exe` aynı paket içinde bulunmalıdır. `DiscordAccess.exe` dosyasını açın; hazır paket için derleme gerekmez. `Baslat.cmd` kaldırıldı.

Paketin [kullanım notları](docs/OKU-BENI.txt) da depoda bulunur.

GitHub'daki **Code > Download ZIP** ile Releases bölümündeki **Source code** dosyaları yalnız kaynak kodudur; program EXE'si ve motor içermez. Geliştirme için bu dosyaları kullanacaksanız önce “Kaynaktan hazırlama” adımlarını tamamlayın. Depo private olduğu sürece hazır indirmeye erişmek için depoya erişimi olan GitHub hesabınızla giriş yapın. [Tüm sürümler](https://github.com/alpertknn/DiscordAccess/releases).

İndirilen ZIP'in SHA-256 değerini `Get-FileHash .\DiscordAccess-v4.22.zip -Algorithm SHA256` ile hesaplayıp aynı Release'teki `SHA256SUMS.txt` ile karşılaştırabilirsiniz. Hash dosyası kod imzası sağlamaz.

## Önceki sürümden geçiş

1. Eski DiscordAccess'te **Windows ile başlat** açıksa önce kapatın. Bu adım, eski klasöre işaret eden başlangıç kaydını kaldırır.
2. Discord'u tamamen kapatın; eski DiscordAccess'ten **Çıkış** seçin.
3. Yeni ZIP'i ayrı, kısa ve kalıcı bir klasöre tamamen çıkarın. Çalışan paketin üzerine yazmayın veya klasörünü taşımayın.
4. Yeni `DiscordAccess.exe` ile bağlantıyı hazırlayın. Otomatik başlangıç istiyorsanız yeni konumda **Windows ile başlat** seçeneğini yeniden açın. Kapalı tercih kendiliğinden açılmaz.

## Gereksinimler

- Windows 10/11 x64 ve .NET Framework 4.8.
- Yazma izniniz olan resmi Discord Stable kurulumu.
- Yalnız kaynaktan derlemek için Windows'un .NET Framework x64 C# derleyicisi.
- Node.js: yalnız JavaScript testleri için.

Yönetici olarak çalıştırmayın. Bu proje Windows'un genel proxy, DNS veya yönlendirme ayarlarını değiştirmez; ağ sürücüsü veya sistem servisi kurmaz.

## English quick start

Requires Windows 10/11 x64, .NET Framework 4.8 and an existing writable official Discord Stable installation. Run as a standard user.

1. Download `DiscordAccess-v4.22.zip` from the [latest release](https://github.com/alpertknn/DiscordAccess/releases/tag/v4.22) and extract the entire package into a short, permanent local folder.
2. Fully close Discord, start `DiscordAccess.exe`, and wait for the ready tray notification.
3. Open Discord from your usual shortcut, or select **Discord'u aç** in the tray menu. The existing installation and profile are used.
4. Before exiting the helper, fully close Discord. When upgrading or moving the package, disable **Windows ile başlat** in the old helper first; enable it in the new location only if you want automatic startup.

The project is unsigned and does not bypass Windows security controls. For contributions, see [CONTRIBUTING.md](CONTRIBUTING.md); for security reports, see [SECURITY.md](SECURITY.md).

## Windows uygulama denetimi

Hazır paket doğrudan EXE ile çalışır. EXE ve motor Windows'un güvenlik değerlendirmesine tabidir; tepsi modu veya açık kaynak lisansı uygulamalara kod imzası sağlamaz. Proje Windows güvenlik ayarlarını değiştirmez ve engel kaldıran komut çalıştırmaz.

## Kaynaktan hazırlama

Depo klasöründe PowerShell açın:

```powershell
powershell -NoProfile -File scripts/Fetch-Engine.ps1
.\Build.cmd
```

Motor betiği hash ile sabitlenmiş sing-box 1.14.3 kaynak kodunu ve Go 1.26.9 derleyicisini indirir; `engine-build/go.mod`, `go.sum` ve `engine-patches/http-limits/manifest.json` ile sabitlenmiş kaynak değişikliklerinden `1.14.3-discordaccess.2` motorunu derler. HTTP değişiklikleri bağlantı başlıklarını, kimlik doğrulama süresini ve bekleyen doğrulanmamış bağlantıları sınırlar; doğrulanmış aktarımın dosya boyutuna veya tünel süresine sınır koymaz. Çıktı EXE'si ayrıca sabit SHA-256 ile doğrulanır. İlk hazırlık birkaç dakika sürebilir; sistem geneline Go kurulmaz. Doğrulanmış aynı motor zaten `engine/sing-box.exe` konumundaysa yeniden derlenmez. Farklı motorun üzerine yazılmaz; eski çalışma klasörünü kullanmak yerine yeni kaynak klasöründe hazırlayın. Hazır paket kullanıcılarının derleme yapması gerekmez.

## Kullanım

1. Discord'u sistem tepsisinden tamamen kapatın. Önceki DiscordAccess açıksa tepsiden **Çıkış** seçin; eski CMD sürümünde `Q` + Enter kullanın.
2. `DiscordAccess.exe` dosyasını açıp sistem tepsisinde bağlantı hazır bildirimini bekleyin.
3. Discord'u kendi simgenizden açın. Uyumlu kişisel kısayol bulunamazsa DiscordAccess tepsisinden **Discord'u aç** seçin. DiscordAccess tepside çalışırken Discord'u kapatıp aynı yöntemle tekrar açabilirsiniz.
4. Çıkarken önce Discord'u tamamen kapatın, ardından DiscordAccess tepsi simgesine sağ tıklayıp **Çıkış** seçin.

EXE'yi tekrar açmak ikinci bağlantı/motor oluşturmaz; mevcut tepsi oturumuna durum isteği iletir. Tepside **Discord'u aç**, **Yeniden bağlan**, **Bağlantıyı kontrol et**, **Windows ile başlat** ve **Çıkış** bulunur. Elle açılışta Discord kendiliğinden başlamaz.

## Windows açılış sırası

İlk başarılı bağlantıdan sonra yalnız mevcut kullanıcı için DiscordAccess otomatik başlangıcı hazırlanır. Discord'un seçili resmi kurulumuna ait tanınan kişisel `Run` kaydı varsa, özgün komut ve etkin/devre dışı tercihi kurtarma kaydında korunur. Windows'a girişte önce DiscordAccess hazırlanır; yalnız Discord'un otomatik açılma tercihi etkinse bağlantı hazır olduktan sonra resmi Discord bir kez açılır. Discord'un otomatik açılması kapalıysa yalnız bağlantı hazırlanır.

Tepside **Windows ile başlat** seçeneğini kapatmak DiscordAccess'in başlangıç kaydını kaldırır ve özgün Discord kaydını kullanıcı değişikliklerinin üzerine yazmadan geri alır. Sonraki elle açılışlar bu kapalı tercihi korur. Normal **Çıkış** seçimi otomatik başlangıç tercihini kapatmaz.

Bağımsız iki `Run` kaydı açılış sırasını garanti etmediği için özgün Discord kaydı bağlantı yardımcısı tarafından koordine edilir ([Windows belgesi](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys)). Tanınan Discord kaydı sonradan tekrar oluşturulursa tepsi oturumu bunu beş saniyede bir kontrol eder. Tanınmayan/değişmiş komutlar veya birden fazla kayıt korunur ve uyarı verilir. Seçili Discord için etkin HKLM/başlangıç klasörü kaydı varsa sıra koordinasyonu kurulmaz; bu kayıtlar değiştirilmez.

Paketi bağlantı açıkken taşımayın. Başlangıç kaydı açıkken başka klasöre geçecekseniz önce tepsiden **Windows ile başlat** seçeneğini kapatın; yeni kalıcı klasörde tekrar etkinleştirin. Uzun başlangıç komutları Windows'un 260 karakter sınırına takılırsa paket için daha kısa klasör kullanın.

Paket dosyasına veya üst klasörlerine başka standart kullanıcıların yazabildiği konumlara otomatik başlangıç kaydı kurulmaz. v4.22, böyle bir konumdan normal elle açılışta paketi `%USERPROFILE%\DiscordAccess\packages\<uygulama-hash>` altına kopyalar ve doğrulanan EXE ile yeniden başlar. Yalnız uygulama, sabit hash'li motor ve paketin üç kullanım/lisans/kaynak metni kopyalanır; profil, ayar, log veya başka dosya taşınmaz. Kaynak paket yerinde kalır; mevcut dosyaların veya klasörlerin izinleri değiştirilmez. Sonraki elle açılışlar aynı içerikteki kişisel kopyayı kullanır.

Kullanıcı klasörü ve kurulumun bütün üst klasörleri de doğrulanır. Güvensiz kullanıcı klasörü, junction/symlink, farklı mevcut dosya veya eksik/yanlış motor durumunda kurulum reddedilir; güvenlik kontrolü atlanmaz. Windows açılışı, geri alma, konsol ve Discord açma kısayolu yeni kurulum oluşturmaz. Önceden kapatılmış **Windows ile başlat** tercihi korunur; yeni konumda isterseniz tepsiden yeniden etkinleştirin.

Sürekli durumda bir DiscordAccess ve bir sing-box motoru çalışır. Discord'un kendi Electron alt süreçleri normaldir; bunlar ek Discord kopyası değildir ve program tarafından kapatılmaz.

Kurulum, doğrulanmış hatırlanan klasör, `%LOCALAPPDATA%\Discord` ve kişisel Discord kısayollarından bulunur. Başka diskte bulunamazsa:

```powershell
.\DiscordAccess.exe --discord-path "D:\Programlar\Discord"
```

Kurulum klasörü, `Discord.exe`, `Update.exe` veya `app-sürüm` klasörü kabul edilir. Yalnız kurulum kökü hatırlanır; sürüm sonraki açılışta yeniden bulunur. Tam disk taraması yapılmaz. PTB/Canary, ağ paylaşımı ve junction/symlink kurulumları desteklenmez.

Önceki konsol akışı gerekirse paket klasöründe `.\DiscordAccess.exe --console` ile kullanılabilir:

| Konsol komutu | İşlev |
| --- | --- |
| `Q` + Enter | Bağlantıyı kapatır; normal Discord sürecini öldürmez. |
| `T` + Enter | Bağlantı tanısı. |
| `D` + Enter | Discord ek dosyası bağlantısıyla indirme. |
| `A` + Enter | Uyumlu kısayol bulunmazsa seçilen resmi Discord'u açma. |

Tepsi modu açıkken konsol indirmesine geçecekseniz önce Discord'u tamamen kapatın ve DiscordAccess tepsisinden **Çıkış** seçin. Paket klasöründe `.\DiscordAccess.exe --console` açın; bağlantı hazır olunca `D` + Enter ile ek dosyası bağlantısını yapıştırın. İkinci EXE açmak açık tepsi oturumunu konsola dönüştürmez.

Discord içindeki ek dosyası indirme desteği korunur. Tanınan modül girişine geçici destek eklenir; normal `settings.json` dosyasındaki iki güncelleme uç noktası oturumun yerel köprüsüne gider. Discord EXE'si, güncelleyici ve `app.asar`/`core.asar` değiştirilmez. Hesap veya profil kopyalanmaz.

## Geri alma ve güncelleme

Discord açıkken DiscordAccess kapatılırsa geri alma kaydı kalabilir. Discord'u tamamen kapatıp aşağıdaki komutu çalıştırın veya bağlantıyı yeniden başlatın:

```powershell
.\DiscordAccess.exe --restore
```

Discord'un uygulama sürümü veya desktop core modülü güncellenirse Discord'u tamamen kapatın, tepsiden **Çıkış** seçin ve `DiscordAccess.exe` dosyasını yeniden açın. Oturum boyunca doğrulanan sürümün dosyaları tutulur; yeni sürüm ve modül desteği sonraki başlangıçta hazırlanır. `--restore`, bağlantı/kısayollar ile birlikte DiscordAccess otomatik başlangıcını da geri alır ve kapalı tercih bırakır.

Dosya işlem sırasında başka bir program tarafından değiştirilirse işlem durabilir. Tanınmayan içerik ve `.discordaccess-fs-*.preserved` / `.rollback-preserved` kurtarma dosyaları korunur; bunları otomatik temizlikle silmeyin veya herkese açık hata kaydına yüklemeyin.

Discord EXE'sini doğrudan açmak ve `discord://` bağlantıları kısayol akışının kapsamına girmez. Koordine edilmiş Windows başlangıcı resmi Discord'u sabit proxy argümanlarıyla açar. Ortak Başlat menüsü kısayolları okunabilir, değiştirilmez. Ses/video UDP akışı doğrudan kalır. Her internet sağlayıcısı veya tüm ses/yayın işlevleri için başarı garantisi verilmez.

Kayıtlar `%LOCALAPPDATA%\DiscordAccess\v3` konumundadır. Bağlantı açıkken paket klasörünü taşımayın. Proje imzasızdır; Smart App Control veya anti-cheat onayı olduğunu iddia etmez.

## Geliştirme

Derleme, testler ve katkı akışı için [CONTRIBUTING.md](CONTRIBUTING.md), sürüm değişiklikleri için [CHANGELOG.md](CHANGELOG.md), güven sınırları için [güvenlik notları](docs/SECURITY-NOTES.md) dosyasına bakın. Yayın hazırlama adımları [RELEASING.md](docs/RELEASING.md) içindedir. v4.22 mevcut resmî Discord/profil/ağ akışını değiştirmez. Loglar, hesap/profil dosyaları ve yerel test çıktıları depoya dahil edilmez.

Normal hatalar için [Issues](https://github.com/alpertknn/DiscordAccess/issues) şablonunu kullanın; program ve Windows sürümünü, yapılan işlemi ve kısa hata kodunu yazın. Güvenlik sorunlarını herkese açık issue veya PR'ye yazmadan önce [SECURITY.md](SECURITY.md) politikasını okuyun. Discord profili, kurtarma `journal.json` dosyası, hesap token'ı veya imzalı dosya bağlantısı paylaşmayın. Yerel kayıtlar kişisel yol içeriyorsa paylaşmadan önce bu kısımları çıkarın.

## Lisans

DiscordAccess'in kendi C#/JavaScript kodu ve kendi derleme araçları [MIT](LICENSE) lisanslıdır. `engine-patches/` altındaki upstream sing-box/sing kaynaklarından türetilen Go dosyaları GPL-3.0-or-later kapsamını korur; bu klasörün MIT olarak yeniden lisanslandığı iddia edilmez. Ayrı motorun ve native bileşenlerin bildirimleri [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) ve [ENGINE-THIRD-PARTY-NOTICES.txt](licenses/ENGINE-THIRD-PARTY-NOTICES.txt), sürüm/kaynak/hash envanteri [engine-source-manifest.json](licenses/engine-source-manifest.json) içindedir. Bileşenlerin kendi lisansları geçerlidir. Hazır paketteki `LISANSLAR.txt` bu metinleri birlikte içerir; karşılık gelen motor kaynak paketi ayrı Release dosyasıdır. Discord uygulaması bu depoda dağıtılmaz; bu proje Discord'un resmi ürünü değildir.
