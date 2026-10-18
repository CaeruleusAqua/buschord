# Test fixtures

`testprojekt-ets6.knxproj` is the public ETS6 password-protected test project from the
xknxproject project (GPL-2.0), used here only as test data:

- Source: https://github.com/XKNX/xknxproject/blob/main/test/resources/testprojekt-ets6.knxproj
- Downloaded: 2026-10-18
- SHA-256: `DCB193844518980FB31429D36CDEC929E17B604BCF09D577094E066D4615FB4E`
- Size: 394270 bytes
- Password: `test`
- Shape: outer plain ZIP with `knx_master.xml` (schema `http://knx.org/xml/project/21`)
  and an inner `P-04BF.zip` whose `0.xml`/`project.xml` are WinZip AES-256 (AE-1).
