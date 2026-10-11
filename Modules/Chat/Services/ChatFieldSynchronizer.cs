namespace SignIt.Modules.Chat.Services;

public static class ChatFieldSynchronizer
{
    public static void SynchronizeAliases(Dictionary<string, string> fields)
    {
        // nama <-> nama_kegiatan
        Sync(fields, "nama", "nama_kegiatan");
        Sync(fields, "nama", "perihal");

        // lokasi <-> ruangan_kegiatan / tempat_kegiatan
        Sync(fields, "lokasi", "ruangan_kegiatan");
        Sync(fields, "lokasi", "tempat_kegiatan");

        // tanggal <-> hari_tanggal_kegiatan / tanggal_kegiatan
        Sync(fields, "tanggal", "hari_tanggal_kegiatan");
        Sync(fields, "tanggal", "tanggal_kegiatan");

        // ketua <-> nama_ketua_pelaksana / nama_pemohon_2
        Sync(fields, "ketua", "nama_ketua_pelaksana");
        Sync(fields, "ketua", "nama_pemohon_2");

        // pembina <-> nama_pembina_ormawa / nama_mengetahui_1
        Sync(fields, "pembina", "nama_pembina_ormawa");
        Sync(fields, "pembina", "nama_mengetahui_1");

        // jenis <-> jenis_permohonan
        Sync(fields, "jenis", "jenis_permohonan");

        // rundown <-> lampiran
        Sync(fields, "rundown", "lampiran");
    }

    private static void Sync(Dictionary<string, string> fields, string key1, string key2)
    {
        if (fields.TryGetValue(key1, out var val1) && !string.IsNullOrWhiteSpace(val1))
        {
            if (!fields.TryGetValue(key2, out var val2) || string.IsNullOrWhiteSpace(val2))
            {
                fields[key2] = val1;
            }
        }
        else if (fields.TryGetValue(key2, out var val2) && !string.IsNullOrWhiteSpace(val2))
        {
            if (!fields.TryGetValue(key1, out var val1From2) || string.IsNullOrWhiteSpace(val1From2))
            {
                fields[key1] = val2;
            }
        }
    }
}
