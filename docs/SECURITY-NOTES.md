# Güvenlik sınırları

v4.21, v4.20'nin aşağıdaki korumalarını ve resmî Discord/profil/ağ akışını korur; bu sürümün ek kapsamı lisans/native kaynak envanteri ve dağıtım belgeleridir. Otomatik kontrollerin geçmesi, yazılımın açık içermediğini veya her Discord/ağ koşulunda çalışacağını kanıtlamaz. Gerçek hesap, ses/yayın ve Windows yeniden başlatma denemesi ayrı doğrulamadır.

## Korunan teknik sınırlar

- Yerel proxy düz HTTP `Upgrade` isteklerini motor kimlik bilgileri eklenmeden reddeder. HTTPS/WSS için mevcut CONNECT akışı korunur.
- Otomatik başlangıçtan önce EXE ve üst klasörlerin sahibi ve etkin yazma/silme izinleri doğrulanır. Başka standart kullanıcıların değiştirebildiği hedefe kalıcı başlangıç kaydı kurulmaz; kullanıcı izinleri değiştirilmez.
- Tek oturum kilidi ve Discord açma/durum/başlangıç bildirimleri, kullanıcı SID'siyle sınırlandırılmış özel Windows nesne ad alanlarında tutulur. Başka kullanıcının aynı görünür adı önceden oluşturması güvenilir oturum sayılmaz. Kullanıcıya özel tek yazarlı dosya kilidi, eski ad alanına tutunan süreçler ve farklı Windows oturumları arasında da ikinci bağlantıyı engeller. Her birincil süreç yeni GUID ile kendi ad alanını oluşturup ömrü boyunca tutar. Keşif kaydı yalnız sürüm/kullanıcı SID'si/Windows oturumu/GUID içerir; doğrulanmış dosya tutamacından en fazla 512 bayt okunur. Kapanışta kayıt aynı kilit tutulurken temizlenir. İstek gönderen süreç yalnız doğrulanmış mevcut ad alanını açar; dosya/ad alanı oluşturmaz veya kilit sahipliğini almaz. `--restore` bildirim dinleyicisi kurmaz.
- Geçici çalıştırılabilir indirme desteği ve kurulum kurtarma kayıtları açılan dosyanın kendi sahibi ve etkin değişiklik izinleriyle doğrulanır. Güvenilir üst klasör tek başına yeterli sayılmaz. Dosya değiştirme sonrası izinler tekrar kontrol edilir; doğrulanan çalıştırılabilir destek oturum boyunca salt okunur dosya tutamacıyla korunur. Bekleyen geri alma kaydı korunurken kapanış dosya tutamacını bırakır.
- Dosya değiştirme işlemi, gerçekten yerinden çıkarılan dosyayı yedekleyip beklenen içerikle karşılaştırır. Çakışmada bilinmeyen veri korunur. Silme, doğrulanmış açık dosyanın tanıtıcısı üzerinden yapılır.
- Motor, kararlı sing-box 1.14.3 kaynakları, sabit bağımlılıklar ve hash ile doğrulanan HTTP kaynak değişiklikleriyle yeniden derlenir. Kaynak arşivi, araç zinciri, bağımlılık kilitleri, kaynak değişiklikleri ve sonuç EXE hash'i doğrulanır. Lisans envanteri dağıtılan EXE'nin Go build bilgisine dayanır.
- Motor HTTP dinleyicisi en fazla 128 bekleyen doğrulanmamış bağlantıyı parser goroutine'i oluşturmadan kabul eder. Kimlik doğrulama için bağlantı başına mutlak 5 saniye, her istek satırı ve başlık toplamı için 64 KiB sınır vardır. Başarısız doğrulama tekrarları süreyi uzatmaz. Başarılı doğrulamadan sonra okuma/yazma süreleri temizlenir ve kapasite serbest bırakılır; dosya gövdeleri ile CONNECT/WSS tünelleri bu boyut/süre sınırlarına tabi değildir. Her HTTP isteğinde kimlik doğrulama tekrar kontrol edilir.

## Kapsam

Program standart kullanıcı yetkisiyle çalışır. Yerel proxy yalnız doğrulanmış Discord süreçleri ve uygulamanın kendi işlemlerine açıktır. Genel Windows proxy/DNS/yönlendirme ayarı, sürücü, TUN veya sistem servisi kurulmaz. İndirilen dosyalar otomatik çalıştırılmaz. Resmî Discord EXE'si ve mevcut profil kullanılır; kısayol hedefi korunur.

Aynı kullanıcı yetkisini ele geçiren zararlı yazılım veya yönetici/SYSTEM saldırganı için ayrı bir güvenlik sınırı iddia edilmez. Dosya işlemleri Windows dosya adı alanında mutlak karşılaştır-ve-değiştir garantisi vermez; çakışma yedekleri kullanıcı içeriğini korumak içindir. Paket kaydı kurulduktan sonra kullanıcı izinleri bağımsız olarak değiştirilirse sonraki Windows açılışını geçmiş bir kontrol güvenceye alamaz.

Bu proje imzasızdır. Açık kaynak lisansı, hash kontrolü ve test başarısı Smart App Control veya anti-cheat onayı sağlamaz. Güvenlik politikaları düşürülmez; engellenen yerel testler farklı yükleme yöntemiyle çalıştırılmaz. GitHub Actions kaldırılmıştır; Windows regresyonları izin verilen geliştirme ortamında elle çalıştırılır. Geçmiş otomatik test kayıtları sonradan değişen kodun doğrulaması sayılmaz.

Güvenlik açığı bildirim yolu ve desteklenen sürüm politikası [SECURITY.md](../SECURITY.md) içindedir. Normal hata bildirimlerinde hesap token'ı, profil, imzalı ek dosyası URL'si, kurtarma journal'ı veya `.preserved` dosyalarını paylaşmayın. Program sürümü, kısa hata kodu ve kişisel verilerden arındırılmış yeniden üretim adımları yeterlidir.

## Lisans ve dağıtım kapsamı

Uygulamanın kendi kodu MIT lisanslıdır; upstream motor kaynakları ve `engine-patches/` içindeki türetilmiş dosyalar kendi GPL-3.0-or-later kapsamını korur. Gömülü native bileşenler ve diğer bağımlılıklar da kendi bildirimleriyle envantere dahil edilir. Lisans metni veya karşılık gelen kaynağın sağlanması, Windows güvenlik onayı veya canlı Discord işlevi doğrulaması değildir. Dağıtım öncesi kontroller [RELEASING.md](RELEASING.md) içindedir.
