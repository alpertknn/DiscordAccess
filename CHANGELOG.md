# Sürüm değişiklikleri

Bu dosya doğrulanmış kaynak geçmişinin kısa özetidir. Tam kaynak sürümleri Git etiketlerinde, paket ve doğrulama ayrıntıları ilgili Release notlarında tutulur. Gerçek hesap, ses/yayın, tepsi etkileşimi ve Windows yeniden başlatması sentetik testlerden ayrı değerlendirilir.

## v4.22

- Başka standart kullanıcıların yazabildiği masaüstü/indirme paketleri, normal elle açılışta doğrulanan kişisel klasöre kopyalanıp oradan çalıştırılır. Windows başlangıcı artık güvenli paket kopyasına kaydedilebilir.
- EXE, sabit hash'li motor ve kullanım/lisans/kaynak metinleriyle sınırlı kopyalama; üst klasör/ACL/içerik doğrulaması ve farklı mevcut dosyayı koruyan işlemler eklendi. Mevcut izinler değiştirilmez.
- Otomatik başlangıç, geri alma, konsol ve Discord açma işlemleri kurulum oluşturmaz; kullanıcı başlangıcı kapattıysa tercih korunur.
- Kişisel paket kopyası, tekrarlı açılış, farklı sürüm, yanlış motor, güvensiz kullanıcı klasörü ve dosya çakışmaları için sentetik regresyon kontrolleri eklendi.
- Motor ve beş dosyalı hazır paket/dört release dosyası yapısı korunur. Resmî Discord/profil/ağ akışı değiştirilmez.

## v4.21

- Motorun WireGuard wrapper/native bileşenlerini de kapsayan lisans ve karşılık gelen kaynak envanteri düzeltildi; hazır dağıtım malzemeleri yenilendi.
- Katkı, güvenlik bildirimi ve yayın hazırlama belgeleri; gizlilik uyarılı issue/PR şablonları eklendi.
- Önceki sürümden geçişte eski başlangıç kaydını kapatma ve yeni kalıcı klasörde yeniden etkinleştirme adımları açıklandı.
- v4.20'nin uygulama akışı ve `1.14.3-discordaccess.2` motoru korunur. Resmî Discord/profil kopyası, genel Windows proxy/DNS ayarı, sürücü veya servis eklenmez.
- Eksik bildirimli eski hazır ikili paketler desteklenen indirme olarak sunulmaz; eski etiketler ve commit geçmişi korunur.

## v4.20

- Çalıştırılabilir indirme desteği ve kurtarma dosyalarının kendi sahiplik/izin kontrolleri güçlendirildi.
- Resident kilidi ve açma/durum/başlangıç kanalları kullanıcı SID'siyle sınırlandırılmış, her birincil süreçte yeni GUID kullanan özel ad alanlarına taşındı.
- Motorun kimlik doğrulama öncesi HTTP başlıkları, mutlak bekleme süresi ve bekleyen bağlantı sayısı sınırlandı; doğrulanmış dosya gövdeleri ve CONNECT/WSS tünelleri korunur.
- GitHub Actions kaldırıldı; Windows doğrulaması açık komutlarla elle yürütülür.

## v4.19

- Düz HTTP Upgrade reddi, başlangıç hedefi güven kontrolleri ve çakışan dosya/kurtarma içeriğini koruyan işlemler eklendi.
- Sabit kaynak ve Go bağımlılıklarıyla motor yeniden derleme yolu hazırlandı.

## v4.18

- Hazır paket ile kaynak ZIP ayrımı, kullanım/PowerShell yönergeleri ve dağıtım bildirimleri geliştirildi.

## v4.17

- Tepsi modu ve resmî Discord'un kişisel Windows başlangıç tercihiyle koordinasyon eklendi.

## v4.16

- Bu deponun ilk izlenen Windows kaynakları ve hazır paket kullanım belgeleri yayımlandı.
