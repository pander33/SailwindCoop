using System;
using System.IO;
using UnityEngine;

namespace SailwindCoop.Avatar
{
    /// <summary>
    /// Голос жестов. Сейчас один звук — выкрик «Land ho!» из <c>sounds/landho.wav</c> рядом с DLL.
    /// WAV разбирается вручную: это один синхронный вызов без UnityWebRequest и корутин.
    /// Нет файла или формат не PCM — жест остаётся беззвучным.
    /// </summary>
    internal static class EmoteAudio
    {
        private const string LandHoFile = "landho.wav";
        private const float Volume = 0.9f;

        private static AudioClip _landHo;
        private static bool _landHoTried;
        private static AudioSource _local;

        /// <summary>Выкрик удалённого игрока: объёмный звук, привязанный к его аватару.</summary>
        public static void PlayLandHoAt(Transform avatarRoot)
        {
            try
            {
                AudioClip clip = LandHo();
                if (clip == null || avatarRoot == null) return;

                Transform voice = avatarRoot.Find("EmoteVoice");
                AudioSource src = voice != null ? voice.GetComponent<AudioSource>() : null;
                if (src == null)
                {
                    var go = new GameObject("EmoteVoice");
                    go.transform.SetParent(avatarRoot, false);
                    src = go.AddComponent<AudioSource>();
                    src.playOnAwake = false;
                    src.spatialBlend = 1f;
                    src.rolloffMode = AudioRolloffMode.Linear;
                    src.minDistance = 3f;
                    src.maxDistance = 150f;   // крик с мачты должен доходить до причала
                    src.dopplerLevel = 0f;
                }
                src.PlayOneShot(clip, Volume);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[EmoteAudio] не удалось проиграть выкрик у аватара: " + e.Message);
            }
        }

        /// <summary>Собственный выкрик: игрок слышит себя без пространственного затухания.</summary>
        public static void PlayLandHoLocal()
        {
            try
            {
                AudioClip clip = LandHo();
                if (clip == null) return;
                if (_local == null)
                {
                    var go = new GameObject("CoopEmoteVoice");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _local = go.AddComponent<AudioSource>();
                    _local.playOnAwake = false;
                    _local.spatialBlend = 0f;
                }
                _local.PlayOneShot(clip, Volume);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[EmoteAudio] не удалось проиграть свой выкрик: " + e.Message);
            }
        }

        private static AudioClip LandHo()
        {
            if (_landHo != null || _landHoTried) return _landHo;
            _landHoTried = true;
            string path = "";
            try
            {
                string dir = Path.GetDirectoryName(Plugin.Instance.Info.Location) ?? "";
                path = Path.Combine(Path.Combine(dir, "sounds"), LandHoFile);
                if (!File.Exists(path))
                {
                    Plugin.Logger.LogWarning("[EmoteAudio] нет файла " + path + " — жест «Land ho!» будет без звука");
                    return null;
                }
                _landHo = LoadWav(File.ReadAllBytes(path), "landho");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[EmoteAudio] не удалось загрузить " + path + ": " + e.Message);
            }
            return _landHo;
        }

        /// <summary>PCM 8/16/24/32 бит и IEEE float 32 бит; остальное — исключение с причиной.</summary>
        private static AudioClip LoadWav(byte[] d, string name)
        {
            if (d.Length < 12 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F' ||
                d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E')
                throw new InvalidDataException("не RIFF/WAVE");

            int format = 0, channels = 0, rate = 0, bits = 0;
            int dataAt = -1, dataLen = 0;
            int i = 12;
            while (i + 8 <= d.Length)
            {
                int size = BitConverter.ToInt32(d, i + 4);
                int body = i + 8;
                if (size < 0 || body + size > d.Length) size = d.Length - body;
                if (d[i] == 'f' && d[i + 1] == 'm' && d[i + 2] == 't' && d[i + 3] == ' ' && size >= 16)
                {
                    format = BitConverter.ToUInt16(d, body);
                    channels = BitConverter.ToUInt16(d, body + 2);
                    rate = BitConverter.ToInt32(d, body + 4);
                    bits = BitConverter.ToUInt16(d, body + 14);
                }
                else if (d[i] == 'd' && d[i + 1] == 'a' && d[i + 2] == 't' && d[i + 3] == 'a')
                {
                    dataAt = body;
                    dataLen = size;
                    break;
                }
                i = body + size + (size & 1);
            }

            bool pcm = format == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
            bool ieee = format == 3 && bits == 32;
            if (!pcm && !ieee)
                throw new InvalidDataException("формат " + format + ", " + bits + " бит не поддержан");
            if (dataAt < 0 || channels < 1 || rate < 1)
                throw new InvalidDataException("нет данных или заголовка fmt");

            int bytesPer = bits / 8;
            int total = dataLen / bytesPer;
            total -= total % channels;
            var samples = new float[total];
            for (int s = 0; s < total; s++)
            {
                int p = dataAt + s * bytesPer;
                switch (bits)
                {
                    case 8: samples[s] = (d[p] - 128) / 128f; break;
                    case 16: samples[s] = BitConverter.ToInt16(d, p) / 32768f; break;
                    case 24: samples[s] = ((d[p] | (d[p + 1] << 8) | ((sbyte)d[p + 2] << 16))) / 8388608f; break;
                    default:
                        samples[s] = ieee ? BitConverter.ToSingle(d, p) : BitConverter.ToInt32(d, p) / 2147483648f;
                        break;
                }
            }

            AudioClip clip = AudioClip.Create(name, total / channels, channels, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
