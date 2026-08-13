using System;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Пауза хоста должна останавливать и клиента.
    ///
    /// Хост останавливает мир штатно — игровым меню (<c>MainMenu.GameToSettings</c> ставит
    /// <c>Time.timeScale = 0</c>) и на время передачи сохранения новому игроку
    /// (<see cref="JoinPause"/>). Всё, чем распоряжается хост, при этом замирает само:
    /// <see cref="BoatSync"/>, <see cref="ItemSync"/> и <see cref="AnchorSync"/> не шлют снапшотов при
    /// нулевом <c>timeScale</c>, а часы воды стоят, потому что их шаг умножается на хостовый масштаб
    /// (<see cref="CrestWaterSync"/>). Не замирал ровно один объект — сам клиент: он продолжал ходить
    /// по палубе остановленного мира, мог сойти с лодки или упасть в воду, а его взаимодействия
    /// уезжали хосту, который ответит на них через неизвестно сколько.
    ///
    /// Поэтому на время паузы хоста клиенту отключается управление персонажем — тот же приём, что у
    /// ведомого сна (<see cref="SleepSync"/>): <c>Refs.SetPlayerControl(false)</c> гасит
    /// <c>OVRPlayerController</c> и <c>CharacterController</c>. Осмотреться мышью можно, уйти — нет.
    ///
    /// Свой <c>Time.timeScale</c> клиент сознательно НЕ трогает. Игровое меню Sailwind запоминает
    /// «доспаузный» масштаб в собственное поле (<c>unpausedTimescale</c>) при открытии и возвращает
    /// его при закрытии; открывшись во время нашей паузы, оно запомнило бы ноль и вернуло бы ноль —
    /// клиент остался бы замороженным насовсем, уже без нашего участия.
    ///
    /// Управление возвращается, как только хост снял паузу ИЛИ пропала связь: остаться замороженным
    /// из-за последнего дошедшего снапшота «хост на паузе» — это софтлок, а не верность хосту.
    /// </summary>
    public sealed class HostPauseSync
    {
        private readonly CoopNet _net;
        private bool _frozen;
        private CoopLog.Repeat _controlReport;

        public HostPauseSync(CoopNet net) { _net = net; }

        /// <summary>Держим ли мы сейчас клиента замороженным. Читают баннер на экране и оверлей.</summary>
        public bool Frozen => _frozen;

        public void Tick(EnvironmentSync env)
        {
            bool want = _net.Role == Role.Client &&
                        _net.State == LinkState.Connected &&
                        env != null && env.HasHostState &&
                        env.HostTimeScale <= 0.0001f;
            SetFrozen(want);
        }

        public void Clear() { SetFrozen(false); }

        private void SetFrozen(bool value)
        {
            if (value == _frozen) return;
            _frozen = value;
            try
            {
                if (value)
                {
                    Refs.SetPlayerControl(false);
                }
                else if (SleepSync.Instance == null || !SleepSync.Instance.ClientAsleep)
                {
                    // Ведомый сон держит управление по своей причине — не возвращать его за него,
                    // иначе спящий клиент вдруг пойдёт гулять с чёрным экраном.
                    Refs.SetPlayerControl(true);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[HostPauseSync] Could not toggle player control",
                                          e.Message, ref _controlReport);
            }
            Plugin.Logger.LogInfo("[HostPauseSync] client " + (value ? "frozen (host paused)" : "released"));
        }
    }
}
