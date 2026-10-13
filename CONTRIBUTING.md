# Katkı rehberi

## Derleme ve kontroller

Windows 10/11 x64 üzerinde .NET Framework 4.8 ve Node.js kullanın. Motor yapılandırma kontrolü için ayrıca PowerShell 7 (`pwsh`) gerekir. Lisans envanteri ve yayın paketleme betikleri için Python 3.11 veya üzeri gerekir. Depo kökünde:

```powershell
powershell -NoProfile -File scripts/Fetch-Engine.ps1
.\Build.cmd
.\Test.cmd
node tests/attachment-download.test.cjs
node tests/external-wrapper.test.cjs
.\tests\shortcuts\Run.cmd
python -B tests/release-packaging.test.py
```

`Test.cmd`, ikinci Node testinin ihtiyaç duyduğu sentetik wrapper dosyalarını `work/` altında üretir; bu nedenle sırayı koruyun. Kısayol testleri ayrı, geçici fixture verileri kullanır. Arka plan ve başlangıç kontrolleri aynı test çalıştırıcısında sahte işlemler, registry ve kayıt verileriyle yapılır. Kontroller gerçek Discord hesabı açmaz; ağ kontrolleri loopback ve sentetik yanıtlarla yapılır. Test çıktıları ve derlenen dosyalar Git dışında kalır.

Windows bir üretilmiş test EXE'sini engellerse bunu tam test başarısı olarak raporlamayın. Hangi grubun çalışmadığını ve kalan kontrolleri PR'da belirtin; Windows güvenlik ayarlarını değiştiren veya engeli aşan kod eklemeyin.

### Windows testlerini elle çalıştırma

GitHub Actions kaldırıldı; push veya PR açılması otomatik test çalıştırmaz. Yukarıdaki derleme/test sırasını, Windows güvenlik politikasının üretilen test dosyalarını çalıştırmaya izin verdiği bir geliştirme ortamında tamamlayın. Engellenen EXE için güvenlik ayarlarını düşürmeyin veya farklı yükleme yöntemi kullanmayın. Çalışmayan grubu geçmiş saymayın.

Motor kaynak testleri için ayrıca:

```powershell
powershell -NoProfile -File scripts/Build-SecurityEngine.ps1 -RunSourceTests
pwsh -NoProfile -File scripts/Test-EngineConfig.ps1 -RunChecks
```

İlk komut sabit kaynak/Go/bağımlılık kilitleri ve tam dosya değişikliklerinden motoru yeniden derler, üç HTTP/listener birim test grubunu sentetik/loopback bağlantılarla çalıştırır ve sonuç hash'ini doğrular. İkinci komut, önceden hazırlanmış sabit motorun altı DNS ayarını yalnız `check` komutuyla sınar; motoru `run` ile başlatmaz ve Discord isteği göndermez. Discord/profil açılmaz. Bu açık seçenekler Windows uygulama denetimini atlamaz.

`engine-build/go.mod`, `go.sum` ve kaynak değişiklikleri byte düzeyinde sabittir; `Build-SecurityEngine.ps1` değişiklik öncesi/sonrası dosya hash'lerini kontrol edip beklenen motor hash'inden farklı normal çıktıyı reddeder. Motor güncellemesinde önce `-ReviewCandidate -RunSourceTests` ile ayrı inceleme çıktısı üretip kaynak farkını, gömülü modülleri ve bilinen açıkları inceleyin; sonra sabitleri birlikte değiştirin. İnceleme çıktısı `-OutFile` ile birleştirilemez ve hazır paket olarak kabul edilmez.

Test raporlarını `work/` altında yerel olarak saklayın; motorun karşılık gelen kaynak/derleme kanıtlarını dağıtımla birlikte sunun. Daha önceki Windows test kayıtları geçmiş sürümlerin kanıtıdır. Tamamlanan kontrolleri kontrol edilen tam commit ile belirtin; gerçek hesapla mesaj, indirme, yayın ve yeniden başlatma denemesi ayrıdır.

## Katkı gönderme

Depoya yazma yetkiniz yoksa GitHub'da fork oluşturun, kendi fork'unuzu klonlayın ve asıl depoyu `upstream` olarak ekleyin:

```powershell
git clone https://github.com/KULLANICI/DiscordAccess.git
cd DiscordAccess
git remote add upstream https://github.com/alpertknn/DiscordAccess.git
git fetch upstream
git switch -c feature/kisa-ad upstream/main
```

`KULLANICI` yerine kendi GitHub adınızı yazın. Değişikliği tamamlayıp ilgili elle çalıştırılan kontrolleri bitirdikten sonra dalı kendi fork'unuza gönderin:

```powershell
git push -u origin feature/kisa-ad
```

GitHub'da asıl deponun `main` dalını hedefleyen bir pull request açın. Güncelleme gerektiğinde `git fetch upstream` ile son değişiklikleri alın; başkasının dalını veya yayımlanmış sürüm etiketini yeniden yazmayın.

Depoya yazma yetkisi olan ekip üyeleri `main` üzerinden kendi iş dalını açabilir:

```powershell
git switch main
git pull --ff-only
git switch -c feature/kisa-ad
```

PR açıklamasında somut sorunu, yeni davranışı, kontrol edilen tam commit'i, kullanılan komutları, sonuçları ve çalışmayan test gruplarını yazın. Bir komutun engellenmesi veya atlanması başarı sayılmaz. Mevcut çalışan sürümü değiştiren büyük düzenlemeleri küçük, incelenebilir değişikliklere ayırın. Normal hatalar için issue şablonlarını, hassas güvenlik bulguları için [SECURITY.md](SECURITY.md) politikasını kullanın.

## Dizinler ve sahip oldukları iş

| Dizin/dosya | Amaç |
| --- | --- |
| `src/` | C# uygulaması; bazı `TEST_BUILD` kontrolleri de burada tutulur. |
| `bridge/` | Uygulamaya kaynak olarak gömülen ek dosyası indirme desteği. |
| `tests/` | Çalıştırıcılar, JavaScript testleri ve sentetik kısayol fixture'ları. |
| `engine-build/` | Byte düzeyinde sabit Go bağımlılık kilitleri. |
| `engine-patches/http-limits/` | Hash ile doğrulanan upstream motor kaynak değişiklikleri ve Go testleri. |
| `scripts/` | Motor hazırlama, yapılandırma kontrolü, lisans toplama ve yayın paketleme. |
| `licenses/` | Kaynak/lisans envanteri ve özgün üçüncü taraf bildirimleri. |
| `docs/` | Kullanım, güven sınırları ve [yayın hazırlama](docs/RELEASING.md). |
| `work/`, `engine/` | Yerel kanıtlar ve hazırlanan motor; Git'e eklenmez. |

Yayın etiketleri ve mevcut commit geçmişi kaynak/derleme izini korur. Tamamlanmış iş dalları, değişiklikleri `main` içinde doğrulandıktan sonra kaldırılabilir; görünümü sadeleştirmek için geçmişi veya eski etiketleri yeniden yazmayın.

## Korunması gereken davranış

- Resmi Discord kısayol hedefini ve mevcut profilini koruyun; helper hedefi veya ayrı kopya kullanmayın.
- Normal elle açılışta Discord'u otomatik başlatmayın. Windows açılışında yalnız korunmuş otomatik başlatma tercihi varsa, bağlantı hazır olduktan sonra bir kez açın. Normal Discord sürecini kapanışta öldürmeyin.
- Aynı kullanıcı için tek bağlantı oturumu ve tek motor bırakın. Tepsi modu açık CMD, ikinci helper veya ayrı Discord kopyası oluşturmasın.
- Başlangıç kaydını yalnız doğrulanmış seçili resmi Discord'un kişisel Run girdisiyle koordine edin. Kayıtları değiştirmeden önce kurtarma kanıtını yazın; kullanıcı değişikliğinin üzerine yazmayın. StartupApproved, HKLM ve başka uygulamaların kayıtlarını değiştirmeyin.
- Genel Windows proxy/DNS/yönlendirme ayarı, TUN, sürücü veya yönetici gereksinimi eklemeyin.
- Proxy erişimini mevcut kullanıcı/oturumda doğrulanmış resmi Discord görüntüleri ve uygulamanın kendi tanı/indirme işlemleriyle sınırlandırın.
- Yol bulma ve kısayol taramasını sınırlı tutun. Kullanıcının değiştirdiği dosya veya ayarların üzerine geri almada yazmayın.
- İndirme adreslerini belirli Discord ek dosyası alanlarıyla sınırlayın; dosyaları otomatik çalıştırmayın.

Bağlantı, yol bulma, kısayol argümanları, modül desteği veya geri alma değişikliklerine uygun anlamlı fixture ekleyin. Anonim Gateway HELLO kontrolü, hesap girişi, mesaj, dosya veya yayın işlevlerinin canlı doğrulaması değildir; sonuçları ayrı belirtin.

## Depoya eklenmeyecek veriler

Token, parola, kod imzalama anahtarı, Discord profili, oturum/kurtarma kaydı, `.lnk`, gerçek kullanıcı yolu, IP veya dosya indirme bağlantısı paylaşmayın. Hata örneklerinde sentetik veriler kullanın. Log klasörleri, motor EXE'si, paketler ve diğer derleme çıktıları `.gitignore` kapsamındadır.

Motor sürümünü değiştirirken indirme URL'si, ZIP/EXE SHA-256 değerleri, uygulamanın bağımlılık doğrulaması ve üçüncü taraf bildirimlerini birlikte güncelleyin. Binary'nin Go build bilgisine göre gerçek modül/sürüm listesini çıkarın; kaynak arşivlerini kayıtlı `h1` toplamlarıyla doğrulayın. Gömülü native kaynakları da envantere katın: Go modül lisansı, içindeki wrapper/DLL lisanslarının tamamını tek başına kapsamayabilir. LICENSE/NOTICE/COPYING metinlerini ve `licenses/engine-source-manifest.json` dosyasını güncelleyin; bulunmayan lisansı tahmin etmeyin.

DiscordAccess'in kendi uygulama/araç kodu MIT, upstream'den türetilen motor dosyaları GPL-3.0-or-later kapsamındadır; native ve diğer bağımlılıkların kendi lisansları korunur. Hazır paketin `LISANSLAR.txt` dosyasına bu bildirimleri birlikte dahil edin. Üretim paketine test veya yerel kayıt dosyalarını dahil etmeyin. Yayın için [RELEASING.md](docs/RELEASING.md) adımlarını izleyin.
