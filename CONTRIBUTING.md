# Katkı rehberi

## Derleme ve kontroller

Windows x64 üzerinde .NET Framework 4.8 ve Node.js kullanın. Depo kökünde:

```powershell
powershell -NoProfile -File scripts/Fetch-Engine.ps1
.\Build.cmd
.\Test.cmd
node tests/attachment-download.test.cjs
node tests/external-wrapper.test.cjs
.\tests\shortcuts\Run.cmd
```

`Test.cmd`, ikinci Node testinin ihtiyaç duyduğu sentetik wrapper dosyalarını `work/` altında üretir; bu nedenle sırayı koruyun. Kısayol testleri ayrı, geçici fixture verileri kullanır. Arka plan ve başlangıç kontrolleri aynı test çalıştırıcısında sahte işlemler, registry ve kayıt verileriyle yapılır. Kontroller gerçek Discord hesabı açmaz; ağ kontrolleri loopback ve sentetik yanıtlarla yapılır. Test çıktıları ve derlenen dosyalar Git dışında kalır.

Windows bir üretilmiş test EXE'sini engellerse bunu tam test başarısı olarak raporlamayın. Hangi grubun çalışmadığını ve kalan kontrolleri PR'da belirtin; Windows güvenlik ayarlarını değiştiren veya engeli aşan kod eklemeyin.

### GitHub üzerinde Windows testleri

`Windows tests` iş akışı, GitHub'ın ayrı Windows x64 ortamında aynı derleme ve test sırasını çalıştırır. `main` dalına push yapıldığında, `main` hedefli PR'larda ve Actions sayfasından elle başlatıldığında çalışır. C# testleri tamamlanmazsa iş akışı başarısız kalır; bağımsız JavaScript ve kısayol kontrolleri mümkün olduğunda yine sonuç verir.

İş akışında depo erişimi salt okunurdur, kişisel secret gerekmez ve güvenlik politikası değiştirilmez. Motor indirilip sabit hash ile doğrulanır; Discord veya motor başlatılmaz. Yalnız test raporları yedi gün saklanır; üretilmiş EXE'ler, sentetik profiller ve diğer `work/` dosyaları artifact'e eklenmez. Bu kontroller gerçek hesapla mesaj, indirme, yayın veya yeniden başlatma denemesinin yerini tutmaz.

## Ekip çalışma akışı

`main` üzerinden kendi iş dalınızı açın:

```powershell
git switch main
git pull --ff-only
git switch -c feature/kisa-ad
```

Değişikliği tamamlayıp ilgili kontrolleri çalıştırdıktan sonra dalı pushlayın ve `main` hedefli bir pull request açın. PR açıklamasında somut sorunu, yeni davranışı, doğrulamayı ve kalan sınırları yazın. Mevcut çalışan sürümü değiştiren büyük düzenlemeleri küçük, incelenebilir değişikliklere ayırın.

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

Motor sürümünü değiştirirken indirme URL'si, ZIP/EXE SHA-256 değerleri, uygulamanın bağımlılık doğrulaması ve üçüncü taraf bildirimlerini birlikte güncelleyin. Binary'nin Go build bilgisine göre gerçek modül/sürüm listesini çıkarın; kaynak arşivlerini kayıtlı `h1` toplamlarıyla doğrulayıp LICENSE/NOTICE/COPYING metinlerini ve `licenses/engine-source-manifest.json` dosyasını güncelleyin. Bulunmayan lisansı tahmin etmeyin. Hazır paketin `LISANSLAR.txt` dosyasına kendi MIT metnini, motor bildirimini ve gömülü bağımlılık bildirimlerini dahil edin. Üretim paketine test veya yerel kayıt dosyalarını dahil etmeyin.
